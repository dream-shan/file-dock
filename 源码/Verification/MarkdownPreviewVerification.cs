using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace FileDockDemo;

internal static class MarkdownPreviewVerification
{
    internal static void Run(Action<bool, string> check)
    {
        const string sample = """
            # 中文教程

            3. 先读说明
            4. 再执行命令
               - 嵌套项目

            | 名称 | 内容 |
            | --- | --- |
            | 选项 | **默认** |

            ~~~xml
            <example>纯文本</example>
            ~~~
            """;
        var viewer = (FlowDocumentScrollViewer)MarkdownPreview.Create(MarkdownPreview.Parse(sample));
        var blocks = viewer.Document.Blocks.Cast<Block>().ToArray();
        check(blocks[0] is Paragraph heading && heading.FontSize > viewer.Document.FontSize &&
              heading.FontWeight == FontWeights.SemiBold && TextOf(heading).Contains("中文教程"),
            "Markdown reading view presents a styled Chinese heading");
        var list = blocks.OfType<List>().Single();
        check(list.MarkerStyle == TextMarkerStyle.Decimal && list.StartIndex == 3 && list.ListItems.Count == 2 &&
              list.ListItems.LastListItem.Blocks.OfType<List>().Any(),
            "Markdown reading view preserves ordered numbering and nested lists");
        var table = blocks.OfType<Table>().Single();
        check(table.Columns.Count == 2 && table.RowGroups[0].Rows.Count == 2 &&
              TextOf(table.RowGroups[0].Rows[1].Cells[1]).Contains("默认"),
            "Markdown reading view lays out pipe tables as native table cells");
        check(blocks.Last() is Paragraph code && code.FontFamily.Source.Contains("Consolas") &&
              TextOf(code).Contains("<example>纯文本</example>"),
            "Markdown fenced code retains its literal characters");
        viewer.Resources["Ink"] = Brushes.White;
        viewer.Resources["ContentSurface"] = Brushes.Black;
        check(Equals(viewer.Document.Foreground, Brushes.White) && Equals(viewer.Background, Brushes.Black),
            "Markdown reading resolves text and background from the enclosing theme");

        var local = (FlowDocumentScrollViewer)MarkdownPreview.Create(
            "![本地图片](file:///does-not-exist.png) [外部链接](https://example.invalid/)\n\n<script>文字</script>");
        string text = new TextRange(local.Document.ContentStart, local.Document.ContentEnd).Text;
        check(text.Contains("本地图片") && text.Contains("外部链接") && text.Contains("<script>文字</script>") &&
              !text.Contains("file:///") && !text.Contains("https://") &&
              !local.Document.Blocks.OfType<Paragraph>().SelectMany(block => block.Inlines).Any(inline => inline is Hyperlink or InlineUIContainer),
            "Markdown reading treats links as labels and HTML as text without activating external resources");

        string longSource = string.Concat(Enumerable.Repeat("paragraph\n\n", 3000));
        var bounded = (FlowDocumentScrollViewer)MarkdownPreview.Create(longSource);
        check(bounded.Document.Blocks.Count < 3000 &&
              new TextRange(bounded.Document.ContentStart, bounded.Document.ContentEnd).Text.Contains("切换到原文"),
            "Very dense Markdown bounds the number of native elements and identifies the limit");
    }

    private static string TextOf(TextElement element) => new TextRange(element.ContentStart, element.ContentEnd).Text;
}
