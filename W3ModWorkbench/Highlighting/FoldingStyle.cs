using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Folding;

namespace W3ModWorkbench.Highlighting;

/// <summary>The look of an editor's fold markers: soft lines in the text's own colour, the accent colour under the mouse and on folded text.</summary>
public static class FoldingStyle
{
    /// <summary>Call once the editor is in a window, so the theme's brushes are found.</summary>
    public static void Apply(TextEditor editor, FrameworkElement theme)
    {
        static SolidColorBrush Frozen(Color c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }
        var text = theme.TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush t ? t.Color : Colors.Gray;
        var accent = theme.TryFindResource("AccentFillColorDefaultBrush") is SolidColorBrush a ? a.Color : Colors.DodgerBlue;
        if (editor.TextArea.LeftMargins.OfType<FoldingMargin>().FirstOrDefault() is { } margin)
        {
            margin.FoldingMarkerBrush = Frozen(Color.FromArgb(0x70, text.R, text.G, text.B));
            margin.FoldingMarkerBackgroundBrush = Brushes.Transparent;
            margin.SelectedFoldingMarkerBrush = Frozen(accent);
            margin.SelectedFoldingMarkerBackgroundBrush = Frozen(Color.FromArgb(0x33, accent.R, accent.G, accent.B));
        }
        FoldingElementGenerator.TextBrush = Frozen(Color.FromArgb(0xCC, accent.R, accent.G, accent.B));
    }
}
