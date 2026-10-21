using ICSharpCode.AvalonEdit;

namespace W3ModWorkbench;

/// <summary>
/// The font size and tab width Settings gives the code and text editors (scripts, XML, JSON, stubs), put on each editor
/// as it's made and on every one open again when Settings is saved.
/// </summary>
public static class EditorLook
{
    private static readonly List<(WeakReference<TextEditor> Editor, bool TabWidth)> Editors = [];

    /// <summary>
    /// Gives an editor the font size and tab width, and keeps it to update when they change. <paramref name="tabWidth"/>
    /// false keeps the editor's own indent (JSON's two spaces, which is how it's written out).
    /// </summary>
    public static void Apply(TextEditor editor, bool tabWidth = true)
    {
        Set(editor, tabWidth);
        Editors.RemoveAll(e => !e.Editor.TryGetTarget(out _));
        if (!Editors.Any(e => e.Editor.TryGetTarget(out var known) && known == editor)) Editors.Add((new WeakReference<TextEditor>(editor), tabWidth));
    }

    /// <summary>Every editor open, after Settings changed.</summary>
    public static void ApplyAll()
    {
        foreach (var (reference, tabWidth) in Editors.ToList())
            if (reference.TryGetTarget(out var editor)) Set(editor, tabWidth);
        Editors.RemoveAll(e => !e.Editor.TryGetTarget(out _));
    }

    private static void Set(TextEditor editor, bool tabWidth)
    {
        editor.FontSize = Math.Clamp(App.Settings.EditorFontSize, 8, 32);
        if (tabWidth) editor.Options.IndentationSize = Math.Clamp(App.Settings.EditorTabWidth, 1, 12);
    }
}
