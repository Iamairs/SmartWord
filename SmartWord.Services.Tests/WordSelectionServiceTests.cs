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

    private static dynamic CreateSelection(int start, int end, string text)
    {
        dynamic selection = new ExpandoObject();
        selection.Start = start;
        selection.End = end;
        selection.Text = text;
        return selection;
    }
}
