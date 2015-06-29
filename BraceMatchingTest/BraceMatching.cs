using System;
using System.Linq;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Language.Intellisense;
using System.Collections.ObjectModel;

// brace matcher
internal class BraceMatchingTagger : ITagger<TextMarkerTag>
{
    ITextView View { get; set; }
    ITextBuffer SourceBuffer { get; set; }
    SnapshotPoint? CurrentChar { get; set; }
    private Dictionary<char, char> m_braceList;

    public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

    internal BraceMatchingTagger(ITextView view, ITextBuffer sourceBuffer)
    {
        //here the keys are the open braces, and the values are the close braces
        m_braceList = new Dictionary<char, char>();
        m_braceList.Add('{', '}');
        m_braceList.Add('[', ']');
        m_braceList.Add('(', ')');
        this.View = view;
        this.SourceBuffer = sourceBuffer;
        this.CurrentChar = null;

       // this.View.Caret.PositionChanged += CaretPositionChanged;
       // this.View.LayoutChanged += ViewLayoutChanged;
    }

    void ViewLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
    {
        if (e.NewSnapshot != e.OldSnapshot) //make sure that there has really been a change
        {
            UpdateAtCaretPosition(View.Caret.Position);
        }
    }

    void CaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
    {
        UpdateAtCaretPosition(e.NewPosition);
    }
    void UpdateAtCaretPosition(CaretPosition caretPosition)
    {
        CurrentChar = caretPosition.Point.GetPoint(SourceBuffer, caretPosition.Affinity);

        if (!CurrentChar.HasValue)
            return;

        var tempEvent = TagsChanged;
        if (tempEvent != null)
            tempEvent(this, new SnapshotSpanEventArgs(new SnapshotSpan(SourceBuffer.CurrentSnapshot, 0,
                SourceBuffer.CurrentSnapshot.Length)));
    }

    public IEnumerable<ITagSpan<TextMarkerTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0)   //there is no content in the buffer 
            yield break;

        //don't do anything if the current SnapshotPoint is not initialized or at the end of the buffer 
        if (!CurrentChar.HasValue || CurrentChar.Value.Position >= CurrentChar.Value.Snapshot.Length)
            yield break;

        //hold on to a snapshot of the current character
        SnapshotPoint currentChar = CurrentChar.Value;

        //if the requested snapshot isn't the same as the one the brace is on, translate our spans to the expected snapshot 
        if (spans[0].Snapshot != currentChar.Snapshot)
        {
            currentChar = currentChar.TranslateTo(spans[0].Snapshot, PointTrackingMode.Positive);
        }

        //get the current char and the previous char 
        char currentText = currentChar.GetChar();
        SnapshotPoint lastChar = currentChar == 0 ? currentChar : currentChar - 1; //if currentChar is 0 (beginning of buffer), don't move it back 
        char lastText = lastChar.GetChar();
        SnapshotSpan pairSpan = new SnapshotSpan();

        if (m_braceList.ContainsKey(currentText))   //the key is the open brace
        {
            char closeChar;
            m_braceList.TryGetValue(currentText, out closeChar);
            if (BraceMatchingTagger.FindMatchingCloseChar(currentChar, currentText, closeChar, View.TextViewLines.Count, out pairSpan) == true)
            {
                yield return new TagSpan<TextMarkerTag>(new SnapshotSpan(currentChar, 1), new TextMarkerTag("blue"));
                yield return new TagSpan<TextMarkerTag>(pairSpan, new TextMarkerTag("blue"));
            }
        }
        else if (m_braceList.ContainsValue(lastText))    //the value is the close brace, which is the *previous* character 
        {
            var open = from n in m_braceList
                       where n.Value.Equals(lastText)
                       select n.Key;
            if (BraceMatchingTagger.FindMatchingOpenChar(lastChar, (char)open.ElementAt<char>(0), lastText, View.TextViewLines.Count, out pairSpan) == true)
            {
                yield return new TagSpan<TextMarkerTag>(new SnapshotSpan(lastChar, 1), new TextMarkerTag("blue"));
                yield return new TagSpan<TextMarkerTag>(pairSpan, new TextMarkerTag("blue"));
            }
        }
    }

    private static bool FindMatchingCloseChar(SnapshotPoint startPoint, char open, char close, int maxLines, out SnapshotSpan pairSpan)
    {
        pairSpan = new SnapshotSpan(startPoint.Snapshot, 1, 1);
        ITextSnapshotLine line = startPoint.GetContainingLine();
        string lineText = line.GetText();
        int lineNumber = line.LineNumber;
        int offset = startPoint.Position - line.Start.Position + 1;

        int stopLineNumber = startPoint.Snapshot.LineCount - 1;
        if (maxLines > 0)
            stopLineNumber = Math.Min(stopLineNumber, lineNumber + maxLines);

        int openCount = 0;
        while (true)
        {
            //walk the entire line 
            while (offset < line.Length)
            {
                char currentChar = lineText[offset];
                if (currentChar == close) //found the close character
                {
                    if (openCount > 0)
                    {
                        openCount--;
                    }
                    else     //found the matching close
                    {
                        pairSpan = new SnapshotSpan(startPoint.Snapshot, line.Start + offset, 1);
                        return true;
                    }
                }
                else if (currentChar == open) // this is another open
                {
                    openCount++;
                }
                offset++;
            }

            //move on to the next line 
            if (++lineNumber > stopLineNumber)
                break;

            line = line.Snapshot.GetLineFromLineNumber(lineNumber);
            lineText = line.GetText();
            offset = 0;
        }

        return false;
    }

    private static bool FindMatchingOpenChar(SnapshotPoint startPoint, char open, char close, int maxLines, out SnapshotSpan pairSpan)
    {
        pairSpan = new SnapshotSpan(startPoint, startPoint);

        ITextSnapshotLine line = startPoint.GetContainingLine();

        int lineNumber = line.LineNumber;
        int offset = startPoint - line.Start - 1; //move the offset to the character before this one 

        //if the offset is negative, move to the previous line 
        if (offset < 0)
        {
            line = line.Snapshot.GetLineFromLineNumber(--lineNumber);
            offset = line.Length - 1;
        }

        string lineText = line.GetText();

        int stopLineNumber = 0;
        if (maxLines > 0)
            stopLineNumber = Math.Max(stopLineNumber, lineNumber - maxLines);

        int closeCount = 0;

        while (true)
        {
            // Walk the entire line 
            while (offset >= 0)
            {
                char currentChar = lineText[offset];

                if (currentChar == open)
                {
                    if (closeCount > 0)
                    {
                        closeCount--;
                    }
                    else // We've found the open character
                    {
                        pairSpan = new SnapshotSpan(line.Start + offset, 1); //we just want the character itself 
                        return true;
                    }
                }
                else if (currentChar == close)
                {
                    closeCount++;
                }
                offset--;
            }

            // Move to the previous line 
            if (--lineNumber < stopLineNumber)
                break;

            line = line.Snapshot.GetLineFromLineNumber(lineNumber);
            lineText = line.GetText();
            offset = line.Length - 1;
        }
        return false;
    }

}

// provider
[Export(typeof(IViewTaggerProvider))]
[ContentType("text")]
[TagType(typeof(TextMarkerTag))]
internal class BraceMatchingTaggerProvider : IViewTaggerProvider
{
    public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
    {
        if (textView == null)
            return null;

        //provide highlighting only on the top-level buffer 
        if (textView.TextBuffer != buffer)
            return null;

        return new BraceMatchingTagger(textView, buffer) as ITagger<T>;
    }
}

internal class TestQuickInfoSource : IQuickInfoSource
{
    private TestQuickInfoSourceProvider m_provider;
    private ITextBuffer m_subjectBuffer;
   // private Dictionary<string, string> m_dictionary;

    private Dictionary<char, char> m_braceList;

    public TestQuickInfoSource(TestQuickInfoSourceProvider provider, ITextBuffer subjectBuffer)
    {
        m_provider = provider;
        m_subjectBuffer = subjectBuffer;

        //these are the method names and their descriptions
      /*  m_dictionary = new Dictionary<string, string>();
        m_dictionary.Add("add", "int add(int firstInt, int secondInt)\nAdds one integer to another.");
        m_dictionary.Add("subtract", "int subtract(int firstInt, int secondInt)\nSubtracts one integer from another.");
        m_dictionary.Add("multiply", "int multiply(int firstInt, int secondInt)\nMultiplies one integer by another.");
        m_dictionary.Add("divide", "int divide(int firstInt, int secondInt)\nDivides one integer by another.");*/

        //here the keys are the open braces, and the values are the close braces
        m_braceList = new Dictionary<char, char>();
        m_braceList.Add('{', '}');
        m_braceList.Add('[', ']');
        m_braceList.Add('(', ')');
    }


    private static bool FindMatchingCloseChar(SnapshotPoint startPoint, char open, char close, int maxLines, out SnapshotSpan pairSpan)
    {
        pairSpan = new SnapshotSpan(startPoint.Snapshot, 1, 1);
        ITextSnapshotLine line = startPoint.GetContainingLine();
        string lineText = line.GetText();
        int lineNumber = line.LineNumber;
        int offset = startPoint.Position - line.Start.Position + 1;

        int stopLineNumber = startPoint.Snapshot.LineCount - 1;
        if (maxLines > 0)
            stopLineNumber = Math.Min(stopLineNumber, lineNumber + maxLines);

        int openCount = 0;
        while (true)
        {
            //walk the entire line 
            while (offset < line.Length)
            {
                char currentChar = lineText[offset];
                if (currentChar == close) //found the close character
                {
                    if (openCount > 0)
                    {
                        openCount--;
                    }
                    else     //found the matching close
                    {
                        pairSpan = new SnapshotSpan(startPoint.Snapshot, line.Start + offset, 1);
                        return true;
                    }
                }
                else if (currentChar == open) // this is another open
                {
                    openCount++;
                }
                offset++;
            }

            //move on to the next line 
            if (++lineNumber > stopLineNumber)
                break;

            line = line.Snapshot.GetLineFromLineNumber(lineNumber);
            lineText = line.GetText();
            offset = 0;
        }

        return false;
    }

    private static bool FindMatchingOpenChar(SnapshotPoint startPoint, char open, char close, int maxLines, out SnapshotSpan pairSpan)
    {
        pairSpan = new SnapshotSpan(startPoint, startPoint);

        ITextSnapshotLine line = startPoint.GetContainingLine();

        int lineNumber = line.LineNumber;
        int offset = startPoint - line.Start - 1; //move the offset to the character before this one 

        //if the offset is negative, move to the previous line 
        if (offset < 0)
        {
            line = line.Snapshot.GetLineFromLineNumber(--lineNumber);
            offset = line.Length - 1;
        }

        string lineText = line.GetText();

        int stopLineNumber = 0;
        if (maxLines > 0)
            stopLineNumber = Math.Max(stopLineNumber, lineNumber - maxLines);

        int closeCount = 0;

        while (true)
        {
            // Walk the entire line 
            while (offset >= 0)
            {
                char currentChar = lineText[offset];

                if (currentChar == open)
                {
                    if (closeCount > 0)
                    {
                        closeCount--;
                    }
                    else // We've found the open character
                    {
                        pairSpan = new SnapshotSpan(line.Start + offset, 1); //we just want the character itself 
                        return true;
                    }
                }
                else if (currentChar == close)
                {
                    closeCount++;
                }
                offset--;
            }

            // Move to the previous line 
            if (--lineNumber < stopLineNumber)
                break;

            line = line.Snapshot.GetLineFromLineNumber(lineNumber);
            lineText = line.GetText();
            offset = line.Length - 1;
        }
        return false;
    }

  public void AugmentQuickInfoSession(IQuickInfoSession session, IList<object> qiContent, out ITrackingSpan applicableToSpan)
    {
        // Map the trigger point down to our buffer.
        SnapshotPoint? subjectTriggerPoint = session.GetTriggerPoint(m_subjectBuffer.CurrentSnapshot);
        if (!subjectTriggerPoint.HasValue)
        {
            applicableToSpan = null;
            return;
        }

        ITextSnapshot currentSnapshot = subjectTriggerPoint.Value.Snapshot;
        SnapshotSpan querySpan = new SnapshotSpan(subjectTriggerPoint.Value, 0);

        //look for occurrences of our QuickInfo words in the span
        ITextStructureNavigator navigator = m_provider.NavigatorService.GetTextStructureNavigator(m_subjectBuffer);
        TextExtent extent = navigator.GetExtentOfWord(subjectTriggerPoint.Value);
        string searchText = extent.Span.GetText();

        //don't do anything if the current SnapshotPoint is not initialized or at the end of the buffer 
        if (!subjectTriggerPoint.HasValue || subjectTriggerPoint.Value.Position > subjectTriggerPoint.Value.Snapshot.Length) {
            applicableToSpan = null;
            return;
        }
           

        //hold on to a snapshot of the current character
        SnapshotPoint currentChar = subjectTriggerPoint.Value;

        //get the current char and the previous char 
        char currentText = currentChar.GetChar();
        SnapshotPoint lastChar = currentChar == 0 ? currentChar : currentChar - 1; //if currentChar is 0 (beginning of buffer), don't move it back 
        char lastText = lastChar.GetChar();
       // SnapshotSpan pairSpan = new SnapshotSpan();

        if (m_braceList.ContainsKey(currentText))   //the key is the open brace
        {
            char closeChar;
            m_braceList.TryGetValue(currentText, out closeChar);

            String key = currentText.ToString();
            int foundIndex = searchText.IndexOf(key, StringComparison.CurrentCultureIgnoreCase);
            if (foundIndex > -1)
            {
                /*applicableToSpan = currentSnapshot.CreateTrackingSpan
                    (
                    querySpan.Start.Add(3).Position, 9, SpanTrackingMode.EdgeInclusive
                    );*/
                applicableToSpan = currentSnapshot.CreateTrackingSpan(2, 2, SpanTrackingMode.EdgeInclusive);
                string value = closeChar.ToString();
                if (value != null)
                    qiContent.Add(value);
                else
                    qiContent.Add("");
                return;
            }
        }

        applicableToSpan = null;
        ///////////////////
    }
   
    private bool m_isDisposed;
    public void Dispose()
    {
        if (!m_isDisposed)
        {
            GC.SuppressFinalize(this);
            m_isDisposed = true;
        }
    }
}

[Export(typeof(IQuickInfoSourceProvider))]
[Name("ToolTip QuickInfo Source")]
[Order(Before = "Default Quick Info Presenter")]
[ContentType("text")]
internal class TestQuickInfoSourceProvider : IQuickInfoSourceProvider
{
    [Import]
    internal ITextStructureNavigatorSelectorService NavigatorService { get; set; }

    [Import]
    internal ITextBufferFactoryService TextBufferFactoryService { get; set; }

    public IQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer)
    {
        return new TestQuickInfoSource(this, textBuffer);
    }
}

internal class TestQuickInfoController : IIntellisenseController
{
    private ITextView m_textView;
    private IList<ITextBuffer> m_subjectBuffers;
    private TestQuickInfoControllerProvider m_provider;
    private IQuickInfoSession m_session;

    ITextBuffer SourceBuffer { get; set; }
    SnapshotPoint? CurrentChar { get; set; }

    internal TestQuickInfoController(ITextView textView, IList<ITextBuffer> subjectBuffers, TestQuickInfoControllerProvider provider)
    {
        m_textView = textView;
        m_subjectBuffers = subjectBuffers;
        m_provider = provider;

        m_textView.MouseHover += this.OnTextViewMouseHover;

        //m_textView.Caret.PositionChanged += CaretPositionChanged;
        //m_textView.LayoutChanged += ViewLayoutChanged;
    }

    private void OnTextViewMouseHover(object sender, MouseHoverEventArgs e)
    {
        //find the mouse position by mapping down to the subject buffer
        SnapshotPoint? point = m_textView.BufferGraph.MapDownToFirstMatch
             (new SnapshotPoint(m_textView.TextSnapshot, e.Position),
            PointTrackingMode.Positive,
            snapshot => m_subjectBuffers.Contains(snapshot.TextBuffer),
            PositionAffinity.Predecessor);

        if (point != null)
        {
            ITrackingPoint triggerPoint = point.Value.Snapshot.CreateTrackingPoint(point.Value.Position,
            PointTrackingMode.Positive);

            if (!m_provider.QuickInfoBroker.IsQuickInfoActive(m_textView))
            {
                m_session = m_provider.QuickInfoBroker.TriggerQuickInfo(m_textView, triggerPoint, true);
            }
        }
    }

    void ViewLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
    {
        if (e.NewSnapshot != e.OldSnapshot) //make sure that there has really been a change
        {
            UpdateAtCaretPosition(m_textView.Caret.Position);
        }
    }

    void CaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
    {
        UpdateAtCaretPosition(e.NewPosition);
    }
    void UpdateAtCaretPosition(CaretPosition caretPosition)
    {
        SnapshotPoint? point = caretPosition.Point.GetPoint(m_textView.TextSnapshot, caretPosition.Affinity);



        if (point != null)
        {
            ITrackingPoint triggerPoint = point.Value.Snapshot.CreateTrackingPoint(point.Value.Position,
            PointTrackingMode.Positive);

            if (!m_provider.QuickInfoBroker.IsQuickInfoActive(m_textView))
            {
                m_session = m_provider.QuickInfoBroker.TriggerQuickInfo(m_textView, triggerPoint, true);
            }
        }
    }

    public void Detach(ITextView textView)
    {
        if (m_textView == textView)
        {
            m_textView.MouseHover -= this.OnTextViewMouseHover;
            m_textView = null;
        }
    }

    public void ConnectSubjectBuffer(ITextBuffer subjectBuffer)
    {
    }

    public void DisconnectSubjectBuffer(ITextBuffer subjectBuffer)
    {
    }
}

[Export(typeof(IIntellisenseControllerProvider))]
[Name("ToolTip QuickInfo Controller")]
[ContentType("text")]
internal class TestQuickInfoControllerProvider : IIntellisenseControllerProvider
{
    [Import]
    internal IQuickInfoBroker QuickInfoBroker { get; set; }

    public IIntellisenseController TryCreateIntellisenseController(ITextView textView, IList<ITextBuffer> subjectBuffers)
    {
        return new TestQuickInfoController(textView, subjectBuffers, this);
    }
}