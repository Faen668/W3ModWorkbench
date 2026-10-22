using System.IO;
using System.Windows;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.ViewModels;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>Making a new mesh in the project from a glTF, from the tree's right-click menu.</summary>
public partial class MainWindow
{
    private async void TreeNewMesh_Click(object sender, RoutedEventArgs e)
    {
        if (await EnsureProjectAsync(L.T("MainWindow.NewMeshNeedsProject")) is not { } project) return;
        var open = new OpenFileDialog { Title = L.T("MainWindow.NewMeshPickGltf"), Filter = L.T("MeshWindow.GltfOpenFilter") };
        if (open.ShowDialog(this) != true) return;

        // it goes in the folder right-clicked when that's in the workspace, else at the workspace's top
        string folder = project.WorkspacePath;
        if (FolderTree.SelectedItem is FolderNode node && node.Parent is not null)
        {
            var root = node;
            while (root.Parent is not null) root = root.Parent;
            string disk = Path.Combine(root.Archive.FilePath, node.FullPath);
            if (Directory.Exists(disk) && project.GamePathOf(disk) is not null) folder = disk;
        }
        Directory.CreateDirectory(folder);
        var save = new SaveFileDialog
        {
            Title = L.T("MainWindow.NewMeshWhere"),
            InitialDirectory = folder,
            FileName = Path.GetFileNameWithoutExtension(open.FileName) + ".w2mesh",
            Filter = L.T("MainWindow.NewMeshFilter"),
        };
        if (save.ShowDialog(this) != true) return;
        await MakeNewMeshAsync(open.FileName, save.FileName);
    }

    /// <summary>Makes a new mesh from a glTF at a file in the open project's workspace, with its pictures, and opens it.</summary>
    private async Task MakeNewMeshAsync(string gltf, string meshFile)
    {
        if (ProjectWorkspace.Current is not { } project) return;
        if (project.GamePathOf(meshFile) is not { } gamePath)
        {
            await Message.Show(L.T("MeshWindow.PickPlaceProjectsWorkspace"), L.T("MainWindow.NewMeshGoesInWorkspace", project.WorkspacePath), MessageKind.Warning,
                primary: L.T("Common.OK"));
            return;
        }

        var locator = new ResourceLocator(OpenArchives(), null, GameLocator.Resolve(null), ProjectFiles);
        NewMeshResult result;
        try
        {
            result = await Task.Run(() => NewMesh.Make(gltf, gamePath, locator.ReadFile));
            Directory.CreateDirectory(Path.GetDirectoryName(meshFile)!);
            var writes = new List<(string File, byte[] Data)> { (meshFile, result.Mesh.File), (meshFile + ".1.buffer", result.Mesh.Buffer) };
            writes.AddRange(result.Pictures.Select(p => (project.GameFilePath(p.GamePath), p.Tga)));
            foreach (var (file, data) in writes)
            {
                FileSafety.CheckWrite(file);
                await File.WriteAllBytesAsync(file, data);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException
            or System.Text.Json.JsonException or ArgumentException or IndexOutOfRangeException or InvalidOperationException or FormatException)
        {
            await Message.Show(L.T("MainWindow.NewMeshFailed"), ex.Message, MessageKind.Warning, primary: L.T("Common.OK"));
            return;
        }

        if (!project.Contains(project.WorkspacePath)) await AddToProjectAsync([project.WorkspacePath]);
        else await ReloadProjectAsync();
        var text = new List<string> { L.T("MainWindow.NewMeshMade", gamePath), string.Join("\n", result.Lines) };
        if (result.Pictures.Count > 0) text.Add(L.T("MainWindow.NewMeshPictures", result.Pictures.Count));
        if (result.Warnings.Count > 0) text.Add(string.Join("\n", result.Warnings.Distinct()));
        MeshWindow.OpenFile(meshFile, locator, this);
        await Message.Show(L.T("MainWindow.NewMeshMadeTitle"), string.Join("\n\n", text), result.Warnings.Count > 0 ? MessageKind.Warning : MessageKind.Info,
            primary: L.T("Common.OK"));
    }
}
