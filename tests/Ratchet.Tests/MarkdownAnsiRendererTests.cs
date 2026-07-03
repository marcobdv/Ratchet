using System.Text.RegularExpressions;
using CodeStack.Ratchet.Cli;
using Xunit;

namespace CodeStack.Ratchet.Tests;

/// <summary>
/// The RATCHET_RENDER=md renderer: markdown string in → ANSI string out. Style tests
/// assert the SGR sequences (full escape from reset, e.g. bold-cyan = ESC[0;1;36m);
/// layout tests strip the invisible sequences and assert the visible text, which is
/// what the terminal shows. The contract throughout: degrade gracefully, never throw.
/// </summary>
public sealed class MarkdownAnsiRendererTests
{
    private static readonly MarkdownAnsiRenderer Renderer = new();

    private static string Render(string md) => Renderer.Render(md);

    /// <summary>What the terminal displays: the output minus SGR codes and OSC 8 wrappers.</summary>
    private static string Visible(string ansi) =>
        Regex.Replace(ansi, @"\x1b\[[0-9;]*m|\x1b\]8;;[^\x1b]*\x1b\\", "");

    // ---- inline styles ----------------------------------------------------------

    [Fact]
    public void Bold_EmitsBoldSgr_AndReturnsToBody()
    {
        var output = Render("some **bold** text");
        Assert.Contains("\x1b[0;1;36mbold", output);       // bold on cyan body
        Assert.Contains("\x1b[0;36m text", output);        // style drops back after
        Assert.Equal("some bold text", Visible(output));   // markers are gone
    }

    [Fact]
    public void Italic_EmitsItalicSgr()
    {
        var output = Render("an *italic* word");
        Assert.Contains("\x1b[0;3;36mitalic", output);
        Assert.Equal("an italic word", Visible(output));
    }

    [Fact]
    public void Strikethrough_EmitsStrikeSgr()
    {
        var output = Render("~~gone~~ kept");
        Assert.Contains("\x1b[0;9;36mgone", output);
        Assert.Equal("gone kept", Visible(output));
    }

    [Fact]
    public void BoldItalic_Nest()
    {
        var output = Render("***both***");
        Assert.Contains("\x1b[0;1;3;36mboth", output);
    }

    [Fact]
    public void InlineCode_StyledDistinctly_BackticksGone()
    {
        var output = Render("call `DoIt()` now");
        Assert.Contains("\x1b[0;93mDoIt()", output);
        Assert.Equal("call DoIt() now", Visible(output));
    }

    // ---- headings -----------------------------------------------------------------

    [Fact]
    public void Headings_StyledByLevel_HashesGone()
    {
        var h1 = Render("# Title");
        Assert.Contains("\x1b[0;1;4;96mTitle", h1);        // h1–h2: bold underline
        Assert.Equal("Title", Visible(h1));

        var h3 = Render("### Sub");
        Assert.Contains("\x1b[0;1;96mSub", h3);            // h3+: bold, no underline
        Assert.Equal("Sub", Visible(h3));
    }

    // ---- code blocks ----------------------------------------------------------------

    [Fact]
    public void FencedCode_FramedWithLanguageLabel()
    {
        var output = Render("```csharp\nvar x = 1;\n```");
        var visible = Visible(output).Split('\n');
        Assert.Equal("┌─ csharp", visible[0]);
        Assert.Equal("│ var x = 1;", visible[1]);
        Assert.Equal("└─", visible[2]);
        Assert.Contains("\x1b[0;97mvar x = 1;", output);   // code text distinct from body
    }

    [Fact]
    public void FencedCode_NoLanguage_StillFramed()
    {
        var visible = Visible(Render("```\nplain\n```")).Split('\n');
        Assert.Equal("┌─", visible[0]);
        Assert.Equal("│ plain", visible[1]);
    }

    [Fact]
    public void FenceHandlerSeam_CustomInfoStringRenderer_ReplacesTheFrame()
    {
        // The seam a future `mermaid` handler slots into: dispatch on the info string.
        var custom = new MarkdownAnsiRenderer(new Dictionary<string, Func<string, string>>
        {
            ["mermaid"] = code => $"<diagram of {code.Trim()}>",
        });
        var output = custom.Render("```mermaid\ngraph TD\n```");
        Assert.Contains("<diagram of graph TD>", output);
        Assert.DoesNotContain("┌─", output);               // the default frame is bypassed

        // Unregistered languages still get the default frame.
        Assert.Contains("┌─ python", Visible(custom.Render("```python\nx = 1\n```")));
    }

    // ---- lists -------------------------------------------------------------------------

    [Fact]
    public void BulletList_RendersBullets()
    {
        var visible = Visible(Render("- one\n- two")).Split('\n');
        Assert.Equal("• one", visible[0]);
        Assert.Equal("• two", visible[1]);
    }

    [Fact]
    public void OrderedList_NumbersFromStart()
    {
        var visible = Visible(Render("3. three\n4. four")).Split('\n');
        Assert.Equal("3. three", visible[0]);
        Assert.Equal("4. four", visible[1]);
    }

    [Fact]
    public void NestedList_Indents()
    {
        var visible = Visible(Render("- outer\n  - inner")).Split('\n');
        Assert.Equal("• outer", visible[0]);
        Assert.Equal("  • inner", visible[1]);
    }

    // ---- blockquote / rule ------------------------------------------------------------

    [Fact]
    public void Blockquote_PrefixedWithBar()
    {
        var visible = Visible(Render("> quoted line"));
        Assert.Equal("│ quoted line", visible);
    }

    [Fact]
    public void HorizontalRule_DrawsALine()
    {
        Assert.Equal(new string('─', 40), Visible(Render("---")));
    }

    // ---- links -----------------------------------------------------------------------

    [Fact]
    public void Link_Osc8Hyperlink_PlusVisibleUrl()
    {
        var output = Render("[docs](https://example.com/x)");
        Assert.Contains("\x1b]8;;https://example.com/x\x1b\\", output);   // OSC 8 open
        Assert.Contains("\x1b]8;;\x1b\\", output);                        // OSC 8 close
        Assert.Equal("docs (https://example.com/x)", Visible(output));    // url survives without OSC 8
    }

    [Fact]
    public void BareUrl_NoRedundantParenthetical()
    {
        var visible = Visible(Render("see https://example.com/x today"));
        Assert.Equal("see https://example.com/x today", visible);
    }

    // ---- tables --------------------------------------------------------------------------

    [Fact]
    public void Table_BoxDrawing_HeaderBold()
    {
        var output = Render("| name | n |\n|---|---|\n| alpha | 1 |");
        var visible = Visible(output).Split('\n');
        Assert.Equal("┌───────┬───┐", visible[0]);
        Assert.Equal("│ name  │ n │", visible[1]);
        Assert.Equal("├───────┼───┤", visible[2]);
        Assert.Equal("│ alpha │ 1 │", visible[3]);
        Assert.Equal("└───────┴───┘", visible[4]);
        Assert.Contains("\x1b[0;1;36mname", output);       // header cells bold
    }

    [Fact]
    public void Table_RightAlignedColumn_PadsLeft()
    {
        var visible = Visible(Render("| num |\n|----:|\n| 7 |")).Split('\n');
        Assert.Equal("│   7 │", visible[3]);
    }

    // ---- multi-block documents ---------------------------------------------------------

    [Fact]
    public void Document_MixedBlocks_AllRender_SeparatedByBlankLines()
    {
        var output = Render("## Plan\n\nSome **steps**:\n\n1. read\n2. write\n\n```sh\nls\n```");
        var visible = Visible(output);
        Assert.Contains("Plan\n\nSome steps:\n\n1. read\n2. write\n\n┌─ sh\n│ ls\n└─", visible);
        Assert.EndsWith("\x1b[0m", output);                // always ends reset
    }

    [Fact]
    public void SoftLineBreak_KeepsTheLineStructure()
    {
        Assert.Equal("line one\nline two", Visible(Render("line one\nline two")));
    }

    // ---- degradation ----------------------------------------------------------------------

    [Fact]
    public void PlainText_JustBodyStyled()
    {
        Assert.Equal("\x1b[0;36mhello\x1b[0m", Render("hello"));
    }

    [Fact]
    public void EmptyAndWhitespace_PassThrough()
    {
        Assert.Equal("", Render(""));
        Assert.Equal("   ", Render("   "));
    }

    [Fact]
    public void UnclosedFence_StillRenders()
    {
        var visible = Visible(Render("```csharp\nvar x = 1;"));
        Assert.Contains("│ var x = 1;", visible);
    }

    [Fact]
    public void HtmlBlock_DoesNotVanish()
    {
        // Raw HTML isn't interpreted, but its text must survive on screen.
        Assert.Contains("<div>hi</div>", Visible(Render("<div>hi</div>")));
    }

    [Fact]
    public void Rendering_IsPure_SameInputSameOutput()
    {
        const string md = "# T\n\n- a\n- **b**\n\n| x |\n|---|\n| 1 |";
        Assert.Equal(Render(md), Render(md));
    }
}
