using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace W3ModWorkbench;

/// <summary>
/// The note above a viewer's properties saying where they came from: REDkit's source file, or the game's cooked file
/// and why some are missing. Hidden until <see cref="Show"/> is told.
/// </summary>
public sealed class SourceNote : Border
{
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85 };

    public SourceNote()
    {
        Padding = new Thickness(10, 7, 10, 7);
        CornerRadius = new CornerRadius(6);
        Visibility = Visibility.Collapsed;
        SetResourceReference(BackgroundProperty, "SubtleFillColorSecondaryBrush");
        var icon = new TextBlock { Text = "", FontSize = 13, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top, Opacity = 0.8 };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        var panel = new DockPanel();
        panel.Children.Add(icon);
        panel.Children.Add(_text);
        Child = panel;
    }

    /// <summary>Says where the properties came from.</summary>
    public void Show(PropertySource source, string? redkitPath, string what)
    {
        _text.Text = RedkitSource.Note(source, redkitPath, what);
        Visibility = Visibility.Visible;
    }
}
