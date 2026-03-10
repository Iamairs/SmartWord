using SmartWord.Services.Selection;
using System.Dynamic;

namespace SmartWord.Services.Tests;

[TestClass]
public sealed class WordSelectionServiceTests
{
    [TestMethod]
    public void GetSelectedText_CollapsedSelection_ReturnsEmptyString()
    {
        dynamic app = new ExpandoObject();
        app.Selection = CreateSelection(12, 12, "鼠标所在段落文本");
        var service = new WordSelectionService(app, null);

        string selected = service.GetSelectedText();

        Assert.AreEqual(string.Empty, selected);
    }

    [TestMethod]
    public void GetSelectedText_ValidRange_ReturnsSelectionText()
    {
        dynamic app = new ExpandoObject();
        app.Selection = CreateSelection(5, 18, "这是一段被选中文本");
        var service = new WordSelectionService(app, null);

        string selected = service.GetSelectedText();

        Assert.AreEqual("这是一段被选中文本", selected);
    }

    [TestMethod]
    public void GetSelectedText_NullSelection_ReturnsEmptyString()
    {
        dynamic app = new ExpandoObject();
        app.Selection = null;
        var service = new WordSelectionService(app, null);

        string selected = service.GetSelectedText();

        Assert.AreEqual(string.Empty, selected);
    }

    [TestMethod]
    public void SelectParagraphRange_ValidRange_UpdatesSelectionRange()
    {
        dynamic app = new ExpandoObject();
        app.Selection = new FakeSelection(CreateSelection(0, 0, string.Empty));
        app.ActiveDocument = new FakeDocument(
            new[]
            {
                new ParagraphRange(1, 10),
                new ParagraphRange(11, 20),
                new ParagraphRange(21, 35),
                new ParagraphRange(36, 48)
            });
        var service = new WordSelectionService(app, null);

        service.SelectParagraphRange(2, 3);

        Assert.AreEqual(11, app.Selection.Start);
        Assert.AreEqual(35, app.Selection.End);
    }

    private static dynamic CreateSelection(int start, int end, string text)
    {
        dynamic selection = new ExpandoObject();
        selection.Start = start;
        selection.End = end;
        selection.Text = text;
        return selection;
    }

    public sealed class FakeSelection
    {
        public FakeSelection(dynamic seed)
        {
            Start = seed == null ? 0 : seed.Start;
            End = seed == null ? 0 : seed.End;
            Text = seed == null ? string.Empty : seed.Text;
            Range = new FakeRange(this);
        }

        public int Start { get; set; }

        public int End { get; set; }

        public string Text { get; set; }

        public FakeRange Range { get; private set; }

        public void SetRange(int start, int end)
        {
            Start = start;
            End = end;
            Range = new FakeRange(this);
        }
    }

    public sealed class FakeDocument
    {
        public FakeDocument(ParagraphRange[] paragraphRanges)
        {
            Paragraphs = new FakeParagraphCollection(paragraphRanges);
        }

        public FakeParagraphCollection Paragraphs { get; private set; }
    }

    public sealed class FakeParagraphCollection
    {
        private readonly FakeParagraph[] _paragraphs;

        public FakeParagraphCollection(ParagraphRange[] paragraphRanges)
        {
            paragraphRanges = paragraphRanges ?? new ParagraphRange[0];
            _paragraphs = new FakeParagraph[paragraphRanges.Length];
            for (int i = 0; i < paragraphRanges.Length; i++)
            {
                _paragraphs[i] = new FakeParagraph(paragraphRanges[i].Start, paragraphRanges[i].End);
            }
        }

        public int Count
        {
            get { return _paragraphs.Length; }
        }

        public FakeParagraph this[int index]
        {
            get { return _paragraphs[index - 1]; }
        }
    }

    public sealed class FakeParagraph
    {
        public FakeParagraph(int start, int end)
        {
            Range = new FakeRange(start, end);
        }

        public FakeRange Range { get; private set; }
    }

    public sealed class FakeRange
    {
        private readonly FakeSelection _selection;

        public FakeRange(int start, int end)
        {
            Start = start;
            End = end;
        }

        public FakeRange(FakeSelection selection)
        {
            _selection = selection;
            Start = selection == null ? 0 : selection.Start;
            End = selection == null ? 0 : selection.End;
        }

        public int Start { get; private set; }

        public int End { get; private set; }

        public void Select()
        {
            if (_selection != null)
            {
                _selection.Start = Start;
                _selection.End = End;
            }
        }
    }

    public sealed class ParagraphRange
    {
        public ParagraphRange(int start, int end)
        {
            Start = start;
            End = end;
        }

        public int Start { get; private set; }

        public int End { get; private set; }
    }
}

