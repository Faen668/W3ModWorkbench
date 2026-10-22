using System.IO;
using System.Numerics;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Scenes;

/// <summary>
/// A camera of a dialog set: where it stands and its angles (pitch tilts it up, yaw turns it round the upright from
/// looking along Y), its lens and depth of field, the slots it films from and to, and the shot it was made as
/// (closeup, medium, wide…) with its tags.
/// </summary>
public sealed record DialogsetCamera(string Name, Vector3 Position, float Pitch, float Yaw, float Roll, float FieldOfView, float Zoom,
    string? Target, string? Source, string? Plane, IReadOnlyList<string> Tags, float FocusNear, float FocusFar, float BlurNear, float BlurFar,
    float DofIntensity, RedObject Object);

/// <summary>
/// A dialog set (.w2dset, a CStorySceneDialogset): the places the actors of a dialogue stand (its slots, each with the
/// stance and face its actor starts in) and the cameras that film them. Scenes name one for each section, and the
/// game stands it where the scene's placement tag says. REDkit's scene editor shows it in 3D with each slot's actor
/// and each camera's name.
/// </summary>
public sealed class DialogsetFile
{
    public required Cr2wFile File { get; init; }
    public required RedObject Resource { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<SceneDialogsetSlot> Slots { get; init; }
    public required IReadOnlyList<DialogsetCamera> Cameras { get; init; }

    public static bool IsDialogset(string path) => path.EndsWith(".w2dset", StringComparison.OrdinalIgnoreCase);

    public static DialogsetFile Read(byte[] data)
    {
        var file = Cr2wFile.Read(data);
        var set = file.First("CStorySceneDialogset") ?? throw new InvalidDataException(L.T("DialogsetFile.NotDialogset"));
        var slots = new List<SceneDialogsetSlot>();
        foreach (var handle in set.Array("slots").OfType<RedHandle>())
            if (file.Object(handle) is { } slot)
            {
                var (position, rotation) = SceneStaging.Transform(slot["slotPlacement"]);
                slots.Add(new SceneDialogsetSlot(slot["slotNumber"] is { } n ? Convert.ToInt32(n) : slots.Count + 1, slot["slotName"] as string ?? "",
                    slot["slotName"] as string, position, rotation, slot));
            }
        var cameras = set.Array("cameras").OfType<RedObject>().Select(c =>
        {
            var (position, rotation) = SceneStaging.Transform(c["cameraTransform"]);
            var made = c.Struct("genParam");
            return new DialogsetCamera(c["cameraName"] as string ?? "", position, rotation.Y, rotation.Z, rotation.X, c.Float("cameraFov", 35), c.Float("cameraZoom"),
                Named(c["targetSlotName"]), Named(c["sourceSlotName"]), made?["cameraPlane"]?.ToString(),
                made?.Array("tags").OfType<string>().ToList() ?? [], c.Float("dofFocusDistNear"), c.Float("dofFocusDistFar"),
                c.Float("dofBlurDistNear"), c.Float("dofBlurDistFar"), c.Float("dofIntensity"), c);
        }).ToList();
        return new DialogsetFile
        {
            File = file,
            Resource = set,
            Name = set["dialogsetName"] as string ?? "",
            Slots = slots.OrderBy(s => s.Number).ToList(),
            Cameras = cameras,
        };
    }

    private static string? Named(object? value) => value as string is { Length: > 0 } s && s != "None" ? s : null;
}
