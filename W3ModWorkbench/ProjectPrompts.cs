using System.IO;
using System.Windows;
using W3ModWorkbench.Core.Projects;

namespace W3ModWorkbench;

/// <summary>
/// The question the editors ask when a file is saved that isn't part of the open project: add it? Each file is asked
/// about once a session, so saying no isn't nagged about. Nothing is asked when no project is open.
/// </summary>
public static class ProjectPrompts
{
    private static readonly HashSet<string> Asked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a saved file is worth asking about: a project is open, the file isn't in it, and it hasn't been asked about yet.</summary>
    public static bool ShouldOffer(string path) =>
        ProjectWorkspace.Current is { } project && !project.Contains(path) && !Asked.Contains(Path.GetFullPath(path));

    /// <summary>Asks, in <paramref name="card"/>, whether to add the saved file to the open project, and does so when the answer is yes.</summary>
    public static async Task OfferAsync(string path, MessageCard card, string saved)
    {
        if (!ShouldOffer(path) || ProjectWorkspace.Current is not { } project) return;
        Asked.Add(Path.GetFullPath(path));
        var choice = await card.Ask($"Add {Path.GetFileName(path)} to {project.Name}?",
            $"{saved} It isn't part of the open project yet. Add it so it's listed with the project's other files.",
            MessageKind.Question, "Add to project", "Not now");
        if (choice == MessageChoice.Primary && Application.Current.MainWindow is MainWindow main)
            await main.AddToProjectAsync([path]);
    }
}
