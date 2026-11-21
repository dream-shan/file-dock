using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using WpfBlock = System.Windows.Documents.Block;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace FileDockDemo;

internal static class MarkdownPreview
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .Build();
    private static readonly FontFamily CodeFont = new("Consolas, Microsoft YaHei UI");

    internal static MarkdownDocument Parse(string source) => Markdown.Parse(source, Pipeline);

    internal static FrameworkElement Create(string source) => Create(Parse(source));

    internal static FrameworkElement Create(MarkdownDocument source)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 14,
            PagePadding = new Thickness(14, 10, 14, 16)
        };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "Ink");
        var context = new DocumentBuilder();
        context.AddBlocks(source, document.Blocks);
        if (context.Truncated)
        {
            var note = new Paragraph(new Run("阅读视图仅显示部分内容。可切换到原文。"));
            note.SetResourceReference(TextElement.ForegroundProperty, "Muted");
            document.Blocks.Add(note);
        }

        var viewer = new FlowDocumentScrollViewer
        {
            Document = document,
            IsToolBarVisible = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        viewer.SetResourceReference(Control.BackgroundProperty, "ContentSurface");
        return viewer;
    }

    private sealed class DocumentBuilder
    {
        private const int MaximumColumns = 16;
        private int _remaining = 2048;
        internal bool Truncated { get; private set; }

        private bool TakeElement()
        {
            if (_remaining > 0)
            {
                _remaining--;
                return true;
            }
            Truncated = true;
            return false;
        }

        internal void AddBlocks(ContainerBlock source, BlockCollection target)
        {
            foreach (var block in source)
            {
                if (!TakeElement()) break;
                if (RenderBlock(block) is { } element) target.Add(element);
            }
        }

        private WpfBlock? RenderBlock(Markdig.Syntax.Block block)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var title = new Paragraph
                    {
                        FontSize = Math.Max(15, 28 - 3 * heading.Level),
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 14, 0, 6)
                    };
                    AddInlines(heading.Inline, title.Inlines);
                    return title;
                case ParagraphBlock prose:
                    var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                    AddInlines(prose.Inline, paragraph.Inlines);
                    return paragraph;
                case CodeBlock code:
                    var preformatted = new Paragraph(new Run(code.Lines.ToString()))
                    {
                        FontFamily = CodeFont,
                        FontSize = 13,
                        Padding = new Thickness(10, 7, 10, 7),
                        Margin = new Thickness(0, 2, 0, 10)
                    };
                    preformatted.SetResourceReference(TextElement.BackgroundProperty, "Surface");
                    return preformatted;
                case ListBlock list:
                    return RenderList(list);
                case Markdig.Extensions.Tables.Table table:
                    return RenderTable(table);
                case QuoteBlock quote:
                    var section = new Section
                    {
                        Margin = new Thickness(0, 2, 0, 8),
                        Padding = new Thickness(10, 0, 0, 0),
                        BorderThickness = new Thickness(3, 0, 0, 0)
                    };
                    section.SetResourceReference(WpfBlock.BorderBrushProperty, "PaneOutline");
                    AddBlocks(quote, section.Blocks);
                    return section;
                case ThematicBreakBlock:
                    var rule = new Paragraph { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 1, 0, 10) };
                    rule.SetResourceReference(WpfBlock.BorderBrushProperty, "PaneOutline");
                    return rule;
                default:
                    return null;
            }
        }

        private List RenderList(ListBlock source)
        {
            var list = new List
            {
                MarkerStyle = source.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Padding = new Thickness(24, 0, 0, 0),
                Margin = new Thickness(0, 2, 0, 8)
            };
            if (source.IsOrdered && int.TryParse(source.OrderedStart, out int start) && start > 0)
                list.StartIndex = start;
            foreach (var sourceItem in source.OfType<ListItemBlock>())
            {
                if (!TakeElement()) break;
                var item = new ListItem();
                AddBlocks(sourceItem, item.Blocks);
                list.ListItems.Add(item);
            }
            return list;
        }

        private WpfTable RenderTable(Markdig.Extensions.Tables.Table source)
        {
            var table = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 10) };
            int columns = Math.Min(source.ColumnDefinitions.Count, MaximumColumns);
            if (source.ColumnDefinitions.Count > columns) Truncated = true;
            for (int column = 0; column < columns; column++)
                table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            var rows = new TableRowGroup();
            table.RowGroups.Add(rows);
            foreach (var sourceRow in source.OfType<Markdig.Extensions.Tables.TableRow>())
            {
                if (!TakeElement()) break;
                var row = new WpfTableRow();
                if (sourceRow.IsHeader)
                {
                    row.FontWeight = FontWeights.SemiBold;
                    row.SetResourceReference(TextElement.BackgroundProperty, "Surface");
                }
                int column = 0;
                foreach (var sourceCell in sourceRow.OfType<Markdig.Extensions.Tables.TableCell>().Take(columns))
                {
                    if (!TakeElement()) break;
                    var cell = new WpfTableCell
                    {
                        Padding = new Thickness(6, 5, 6, 4),
                        BorderThickness = new Thickness(column == 0 ? 1 : 0, rows.Rows.Count == 0 ? 1 : 0, 1, 1)
                    };
                    cell.SetResourceReference(WpfTableCell.BorderBrushProperty, "PaneOutline");
                    AddBlocks(sourceCell, cell.Blocks);
                    foreach (WpfBlock child in cell.Blocks)
                    {
                        child.Margin = new Thickness(0);
                        child.TextAlignment = source.ColumnDefinitions[column].Alignment switch
                        {
                            TableColumnAlign.Center => TextAlignment.Center,
                            TableColumnAlign.Right => TextAlignment.Right,
                            _ => TextAlignment.Left
                        };
                    }
                    row.Cells.Add(cell);
                    column++;
                }
                if (row.Cells.Count > 0) rows.Rows.Add(row);
            }
            return table;
        }

        private void AddInlines(ContainerInline? source, InlineCollection target)
        {
            for (var node = source?.FirstChild; node != null; node = node.NextSibling)
            {
                if (!TakeElement()) break;
                switch (node)
                {
                    case LiteralInline literal:
                        target.Add(new Run(literal.Content.ToString()));
                        break;
                    case HtmlEntityInline entity:
                        target.Add(new Run(entity.Transcoded.ToString()));
                        break;
                    case CodeInline code:
                        var span = new Run(code.Content) { FontFamily = CodeFont };
                        span.SetResourceReference(TextElement.BackgroundProperty, "Surface");
                        target.Add(span);
                        break;
                    case LineBreakInline newline:
                        if (newline.IsHard) target.Add(new LineBreak());
                        else target.Add(new Run(" "));
                        break;
                    case EmphasisInline emphasis:
                        Span formatted = emphasis.DelimiterCount == 2 ? new Bold() : new Italic();
                        AddInlines(emphasis, formatted.Inlines);
                        target.Add(formatted);
                        break;
                    case AutolinkInline autolink:
                        target.Add(new Run(autolink.Url));
                        break;
                    case LinkInline link:
                        // Render the label as inert text.
                        AddInlines(link, target);
                        break;
                }
            }
        }
    }
}
