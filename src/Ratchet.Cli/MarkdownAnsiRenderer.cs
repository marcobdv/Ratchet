using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace CodeStack.Ratchet.Cli;

/// <summary>
/// Renders a complete markdown document to ANSI-styled terminal text. Pure: markdown
/// string in, styled string out — no console I/O, so it is unit-testable and reusable.
///
/// Design notes:
///  - Markdig parses to an AST; this class is the "terminal renderer" half. Anything the
///    walker doesn't recognise falls back to the block's raw source text, and any
///    parse/render exception falls back to the raw input — never worse than raw.
///  - Styles are emitted as full SGR sequences from reset (<c>ESC[0;…m</c>) on every
///    change, so nesting needs no push/pop bookkeeping and assertions stay simple.
///  - Fenced code blocks dispatch on their info string through <see cref="_fenceRenderers"/>;
///    that map is the seam where a future handler (e.g. <c>mermaid</c>) slots in without
///    touching the walker.
/// </summary>
public sealed class MarkdownAnsiRenderer
{
    private const string Reset = "\x1b[0m";

    // Palette: body text stays cyan (the assistant's voice in raw mode); accents around it.
    private static readonly Style Body = new(Fg: "36");
    private static readonly Style HeadingTop = new(Bold: true, Underline: true, Fg: "96");   // h1–h2
    private static readonly Style HeadingSub = new(Bold: true, Fg: "96");                    // h3+
    private static readonly Style InlineCode = new(Fg: "93");
    private static readonly Style CodeText = new(Fg: "97");
    private static readonly Style Dim = new(Fg: "90");                                       // frames, bullets, urls
    private static readonly Style Link = new(Underline: true, Fg: "94");

    // Matches SGR sequences and OSC 8 hyperlink wrappers — everything invisible on screen.
    private static readonly Regex Invisible = new(@"\x1b\[[0-9;]*m|\x1b\]8;;[^\x1b]*\x1b\\", RegexOptions.Compiled);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()   // ~~strikethrough~~
        .UseAutoLinks()
        .Build();

    private readonly IReadOnlyDictionary<string, Func<string, string>> _fenceRenderers;

    /// <param name="fenceRenderers">
    /// Custom renderers per fence info string (first word, case-insensitive), e.g. a future
    /// <c>"mermaid"</c> → diagram drawer. The handler gets the fence body and returns the
    /// fully rendered lines; unhandled languages get the default framed style.
    /// </param>
    public MarkdownAnsiRenderer(IReadOnlyDictionary<string, Func<string, string>>? fenceRenderers = null)
        => _fenceRenderers = fenceRenderers ?? new Dictionary<string, Func<string, string>>();

    public string Render(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return markdown;
        try
        {
            var doc = Markdown.Parse(markdown, Pipeline);
            var ctx = new Ctx(markdown);
            WriteBlocks(ctx, doc, prefix: "", separated: true);
            return ctx.Sb.ToString().TrimEnd('\n') + Reset;
        }
        catch
        {
            return markdown;   // degrade to the raw text, never crash the observer
        }
    }

    // ---- blocks ---------------------------------------------------------------

    private void WriteBlocks(Ctx ctx, IEnumerable<Block> blocks, string prefix, bool separated)
    {
        var first = true;
        foreach (var block in blocks)
        {
            if (block is LinkReferenceDefinitionGroup) continue;
            if (!first && separated) WriteLine(ctx, prefix, trimmed: true);
            first = false;
            WriteBlock(ctx, block, prefix);
        }
    }

    private void WriteBlock(Ctx ctx, Block block, string prefix)
    {
        switch (block)
        {
            case HeadingBlock h:
                WritePrefix(ctx, prefix);
                WriteInlines(ctx, h.Inline, h.Level <= 2 ? HeadingTop : HeadingSub, prefix);
                EndLine(ctx);
                break;

            case ParagraphBlock p:
                WritePrefix(ctx, prefix);
                WriteInlines(ctx, p.Inline, Body, prefix);
                EndLine(ctx);
                break;

            case QuoteBlock q:
                WriteBlocks(ctx, q, prefix + "│ ", separated: true);
                break;

            case ListBlock list:
                WriteList(ctx, list, prefix);
                break;

            case FencedCodeBlock f:
                WriteCodeBlock(ctx, ExtractLines(f), (f.Info ?? "").Split(' ')[0].Trim(), prefix);
                break;

            case CodeBlock c:   // indented code block — no info string
                WriteCodeBlock(ctx, ExtractLines(c), "", prefix);
                break;

            case Table t:
                WriteTable(ctx, t, prefix);
                break;

            case ThematicBreakBlock:
                WritePrefix(ctx, prefix);
                ctx.Style(Dim);
                ctx.Raw(new string('─', 40));
                EndLine(ctx);
                break;

            case ContainerBlock container:   // unknown container — render its children
                WriteBlocks(ctx, container, prefix, separated: true);
                break;

            default:   // unknown leaf — fall back to its raw source text
                WritePrefix(ctx, prefix);
                ctx.Style(Body);
                ctx.Raw(SourceOf(ctx, block).ReplaceLineEndings("\n").Replace("\n", "\n" + prefix));
                EndLine(ctx);
                break;
        }
    }

    private void WriteList(Ctx ctx, ListBlock list, string prefix)
    {
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        var first = true;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            if (!first && list.IsLoose) WriteLine(ctx, prefix, trimmed: true);
            first = false;

            var bullet = list.IsOrdered ? $"{number++}." : "•";
            var childPrefix = prefix + new string(' ', bullet.Length + 1);

            // The bullet and the first block share a line; subsequent blocks hang-indent.
            WritePrefix(ctx, prefix);
            ctx.Style(Dim);
            ctx.Raw(bullet + " ");

            var blocks = item.ToList();
            if (blocks.Count == 0) { EndLine(ctx); continue; }

            if (blocks[0] is ParagraphBlock p)
            {
                WriteInlines(ctx, p.Inline, Body, childPrefix);
                EndLine(ctx);
            }
            else
            {
                EndLine(ctx);
                WriteBlock(ctx, blocks[0], childPrefix);
            }

            foreach (var rest in blocks.Skip(1))
            {
                if (list.IsLoose && rest is not ListBlock) WriteLine(ctx, childPrefix, trimmed: true);
                WriteBlock(ctx, rest, childPrefix);
            }
        }
    }

    private void WriteCodeBlock(Ctx ctx, string code, string language, string prefix)
    {
        // The seam for language-specific fence renderers (a future `mermaid` handler
        // registers here); everything else gets the default dim frame + label.
        if (language.Length > 0 && _fenceRenderers.TryGetValue(language.ToLowerInvariant(), out var custom))
        {
            foreach (var line in custom(code).ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'))
            {
                WritePrefix(ctx, prefix);
                ctx.Raw(line);
                EndLine(ctx);
            }
            return;
        }

        WritePrefix(ctx, prefix);
        ctx.Style(Dim);
        ctx.Raw(language.Length > 0 ? $"┌─ {language}" : "┌─");
        EndLine(ctx);
        foreach (var line in code.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n'))
        {
            WritePrefix(ctx, prefix);
            ctx.Style(Dim);
            ctx.Raw("│ ");
            ctx.Style(CodeText);
            ctx.Raw(line);
            EndLine(ctx);
        }
        WritePrefix(ctx, prefix);
        ctx.Style(Dim);
        ctx.Raw("└─");
        EndLine(ctx);
    }

    private void WriteTable(Ctx ctx, Table table, string prefix)
    {
        // Render every cell first (with inline styling), then size columns on the
        // *visible* text — the SGR/OSC sequences take no width on screen.
        var rows = new List<(bool header, List<string> cells)>();
        foreach (var row in table.OfType<TableRow>())
        {
            var cells = new List<string>();
            foreach (var cell in row.OfType<TableCell>())
            {
                var cellCtx = new Ctx(ctx.Source);
                foreach (var para in cell.OfType<ParagraphBlock>())
                {
                    if (cellCtx.Sb.Length > 0) cellCtx.Raw(" ");
                    WriteInlines(cellCtx, para.Inline, row.IsHeader ? Body with { Bold = true } : Body, "");
                }
                cells.Add(cellCtx.Sb.ToString());
            }
            rows.Add((row.IsHeader, cells));
        }
        if (rows.Count == 0) return;

        var columns = rows.Max(r => r.cells.Count);
        var widths = new int[columns];
        foreach (var (_, cells) in rows)
            for (var i = 0; i < cells.Count; i++)
                widths[i] = Math.Max(widths[i], VisibleLength(cells[i]));

        void Border(char left, char mid, char right)
        {
            WritePrefix(ctx, prefix);
            ctx.Style(Dim);
            ctx.Raw(left + string.Join(mid, widths.Select(w => new string('─', w + 2))) + right);
            EndLine(ctx);
        }

        Border('┌', '┬', '┐');
        for (var r = 0; r < rows.Count; r++)
        {
            var (header, cells) = rows[r];
            WritePrefix(ctx, prefix);
            for (var i = 0; i < columns; i++)
            {
                ctx.Style(Dim);
                ctx.Raw("│ ");
                var cell = i < cells.Count ? cells[i] : "";
                var pad = widths[i] - VisibleLength(cell);
                var align = i < table.ColumnDefinitions.Count ? table.ColumnDefinitions[i].Alignment : null;
                var (lead, trail) = align switch
                {
                    TableColumnAlign.Right => (pad, 0),
                    TableColumnAlign.Center => (pad / 2, pad - pad / 2),
                    _ => (0, pad),
                };
                ctx.Raw(new string(' ', lead));
                ctx.RawStyled(cell);
                ctx.Raw(new string(' ', trail) + " ");
            }
            ctx.Style(Dim);
            ctx.Raw("│");
            EndLine(ctx);
            if (header && r + 1 < rows.Count) Border('├', '┼', '┤');
        }
        Border('└', '┴', '┘');
    }

    // ---- inlines --------------------------------------------------------------

    private void WriteInlines(Ctx ctx, ContainerInline? container, Style style, string prefix)
    {
        if (container is null) return;
        foreach (var inline in container)
            WriteInline(ctx, inline, style, prefix);
    }

    private void WriteInline(Ctx ctx, Inline inline, Style style, string prefix)
    {
        switch (inline)
        {
            case LiteralInline lit:
                ctx.Style(style);
                ctx.Raw(lit.Content.ToString());
                break;

            case EmphasisInline em:
                var emphasised = em.DelimiterChar == '~'
                    ? style with { Strike = true }
                    : em.DelimiterCount >= 2 ? style with { Bold = true } : style with { Italic = true };
                WriteInlines(ctx, em, emphasised, prefix);
                break;

            case CodeInline code:
                ctx.Style(InlineCode);
                ctx.Raw(code.Content);
                break;

            case LinkInline link:
                WriteLink(ctx, link, style, prefix);
                break;

            case AutolinkInline auto:
                WriteHyperlink(ctx, auto.Url, () => { ctx.Style(Link); ctx.Raw(auto.Url); });
                break;

            case LineBreakInline:
                EndLine(ctx);
                WritePrefix(ctx, prefix);
                break;

            case HtmlEntityInline entity:
                ctx.Style(style);
                ctx.Raw(entity.Transcoded.ToString());
                break;

            case HtmlInline html:   // raw tags pass through dimmed rather than vanishing
                ctx.Style(Dim);
                ctx.Raw(html.Tag);
                break;

            case ContainerInline container:   // unknown container — render its children
                WriteInlines(ctx, container, style, prefix);
                break;

            default:   // unknown leaf — fall back to its raw source text
                ctx.Style(style);
                ctx.Raw(SourceOf(ctx, inline));
                break;
        }
    }

    private void WriteLink(Ctx ctx, LinkInline link, Style style, string prefix)
    {
        var url = link.Url ?? "";
        var labelCtx = new Ctx(ctx.Source);
        WriteInlines(labelCtx, link, link.IsImage ? style : Link, prefix);
        var label = labelCtx.Sb.ToString();

        if (link.IsImage)
        {
            ctx.Style(Dim);
            ctx.Raw("[image: ");
            ctx.RawStyled(label);
            ctx.Style(Dim);
            ctx.Raw($"] ({url})");
            return;
        }

        WriteHyperlink(ctx, url, () => ctx.RawStyled(label));
        // The visible URL matters in a terminal without OSC 8 support — show it unless
        // the label already is the URL.
        if (Invisible.Replace(label, "") != url && url.Length > 0)
        {
            ctx.Style(Dim);
            ctx.Raw($" ({url})");
        }
    }

    // OSC 8 terminal hyperlink (Windows Terminal supports it; others ignore the wrapper).
    private static void WriteHyperlink(Ctx ctx, string? url, Action writeLabel)
    {
        var wrap = !string.IsNullOrEmpty(url);
        if (wrap) ctx.Raw($"\x1b]8;;{url}\x1b\\");
        writeLabel();
        if (wrap) ctx.Raw("\x1b]8;;\x1b\\");
    }

    // ---- plumbing ---------------------------------------------------------------

    private static void WritePrefix(Ctx ctx, string prefix)
    {
        if (prefix.Length == 0) return;
        ctx.Style(Dim);
        ctx.Raw(prefix);
    }

    private static void WriteLine(Ctx ctx, string prefix, bool trimmed)
    {
        WritePrefix(ctx, trimmed ? prefix.TrimEnd() : prefix);
        EndLine(ctx);
    }

    private static void EndLine(Ctx ctx) => ctx.Raw("\n");

    private static string ExtractLines(LeafBlock block)
    {
        var sb = new StringBuilder();
        var lines = block.Lines.Lines;
        for (var i = 0; i < block.Lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            sb.Append(lines[i].Slice.ToString());
        }
        return sb.ToString();
    }

    private static string SourceOf(Ctx ctx, MarkdownObject obj)
    {
        var span = obj.Span;
        return span.Start >= 0 && span.End < ctx.Source.Length && span.Length > 0
            ? ctx.Source.Substring(span.Start, span.Length)
            : "";
    }

    private static int VisibleLength(string s) => Invisible.Replace(s, "").Length;

    /// <summary>SGR attribute set; renders as one full escape from reset, so no nesting state.</summary>
    private readonly record struct Style(bool Bold = false, bool Italic = false, bool Strike = false,
        bool Underline = false, string Fg = "")
    {
        public string ToSgr() =>
            "\x1b[0" + (Bold ? ";1" : "") + (Italic ? ";3" : "") + (Underline ? ";4" : "")
                     + (Strike ? ";9" : "") + (Fg.Length > 0 ? ";" + Fg : "") + "m";
    }

    /// <summary>Per-render state: the output, the source (for raw fallbacks), the last SGR emitted.</summary>
    private sealed class Ctx
    {
        public readonly StringBuilder Sb = new();
        public readonly string Source;
        private string _lastSgr = "";

        public Ctx(string source) => Source = source;

        public void Style(in Style s)
        {
            var sgr = s.ToSgr();
            if (sgr == _lastSgr) return;
            Sb.Append(sgr);
            _lastSgr = sgr;
        }

        /// <summary>Plain text — must not itself contain SGR codes (see <see cref="RawStyled"/>).</summary>
        public void Raw(string text) => Sb.Append(text);

        /// <summary>Pre-styled text (from a nested render); invalidates the SGR dedupe state.</summary>
        public void RawStyled(string text)
        {
            Sb.Append(text);
            _lastSgr = "\0";   // unknown trailing style — force the next Style() to emit
        }
    }
}
