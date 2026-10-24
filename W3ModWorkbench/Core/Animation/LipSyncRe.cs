using System.IO;
using W3ModWorkbench.Core.Hdf5;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.Core.Animation;

/// <summary>
/// A lip sync as REDkit's <c>.re</c> (an HDF5 file, as REDkit's Blender plugin and its lip sync generator read and
/// write them): the face's controls in <c>.re_curve_node</c>, a row a frame (<c>trackdata</c>), their names
/// (<c>tracknames</c>) and the same values frame by frame (<c>curvekeys</c>), with a skeleton and animation clip for the
/// neck and head. Laid out as REDkit's own <c>head_anim.re</c>, with all 148 of the face's controls in its order:
/// REDkit's files keep the first 145, and the head's three (head1-3, which move in nearly every one of the game's
/// lines) are kept too so nothing is lost; REDkit's Blender plugin passes over a control the face has no shape key for.
/// </summary>
public static class LipSyncRe
{
    /// <summary>
    /// The face's 148 controls in the order a lip sync keeps them: every human face of the game's (208 in REDkit's
    /// data) has these; a <c>.re</c> has the first 145, the last three being the head's.
    /// </summary>
    public static readonly string[] ControlNames =
    [
        "default", "jaw_open_a", "jaw_open_o", "jaw_chew", "jaw_front", "jaw_back", "jaw_up", "jaw_right", "jaw_left",
        "corner_right_tight", "corner_left_tight", "corner_right_spread", "corner_left_spread", "corner_right_out", "corner_left_out",
        "corner_right_in", "corner_left_in", "corner_right_up", "corner_left_up", "corner_right_down", "corner_left_down",
        "lips_in", "lips_out", "lips_up", "lips_down", "lips_right", "lips_left", "lip_upper_front", "lip_lower_front",
        "lip_upper_back", "lip_lower_back", "lip_upper_out", "lip_lower_out", "lips_blow", "lip_lower_center_up",
        "lip_lower_right_up", "lip_lower_left_up", "lip_lower_center_down", "lip_lower_right_down", "lip_lower_left_down",
        "lip_lower_in", "lip_upper_center_up", "lip_upper_right_up", "lip_upper_left_up", "lip_upper_center_down",
        "lip_upper_right_down", "lip_upper_left_down", "lip_upper_in", "cheek_right_up", "cheek_left_up", "cheek_right_down",
        "cheek_left_down", "brow_right_up", "brow_left_up", "brow_right_down", "brow_left_down", "brow_right_in", "brow_left_in",
        "brow_inner_right_up", "brow_inner_left_up", "brow_middle_right_up", "brow_middle_left_up", "brow_outer_right_up",
        "brow_outer_left_up", "nose_right", "nose_left", "nose_up", "nose_down", "nostrils_out", "thyroid_up", "ears_back",
        "eye_right_right", "eye_left_right", "eye_right_left", "eye_left_left", "eye_right_up", "eye_left_up", "eye_right_down",
        "eye_left_down", "iris_wide", "iris_narrow", "eyelids_upper_right_down", "eyelids_upper_left_down", "eyelids_upper_right_up",
        "eyelids_upper_left_up", "eyelids_lower_right_up", "eyelids_lower_left_up", "eyelids_lower_right_down",
        "eyelids_lower_left_down", "eyelid_upper_inner_left_down", "eyelid_upper_inner_right_down", "eyelid_upper_center_left_down",
        "eyelid_upper_center_right_down", "eyelid_upper_outer_left_down", "eyelid_upper_outer_right_down", "eyelid_lower_inner_left_up",
        "eyelid_lower_inner_right_up", "eyelid_lower_center_left_up", "eyelid_lower_center_right_up", "eyelid_lower_outer_left_up",
        "eyelid_lower_outer_right_up", "eyelid_upper_inner_left_up", "eyelid_upper_inner_right_up", "eyelid_upper_center_left_up",
        "eyelid_upper_center_right_up", "eyelid_upper_outer_left_up", "eyelid_upper_outer_right_up", "eyelid_lower_inner_left_down",
        "eyelid_lower_inner_right_down", "eyelid_lower_center_left_down", "eyelid_lower_center_right_down",
        "eyelid_lower_outer_left_down", "eyelid_lower_outer_right_down", "tongue_front", "tongue_up", "tongue_down", "tongue_left",
        "tongue_right", "tongue_roll", "tongue_wide", "tongue_up_flat", "tongue_turn_left", "tongue_turn_right", "tongue_low",
        "tongue_back", "empty_pose_03", "empty_pose_04", "empty_pose_05", "empty_pose_06", "normal0", "normal1", "normal2",
        "normal3", "normal4", "normal5", "normal6", "normal7", "normal8", "normal9", "normal10", "normal11", "normal12",
        "normal13", "normal14", "normal15", "head1", "head2", "head3",
    ];

    /// <summary>How many controls REDkit's own <c>.re</c> files keep: the face's, without the head's last three.</summary>
    public const int RedkitControlCount = 145;

    /// <summary>The frames a second REDkit's <c>.re</c> files are in: a length is (frames - 1) / 30.</summary>
    public const float ReFramesPerSecond = 30;

    // the marks REDkit's files give each group: an attribute named as the group, 'm' (asset nodes), 'v' (the curve node)
    private const sbyte AssetMark = 109, CurveMark = 118, NodeType = 108;

    /// <summary>
    /// A lip sync from a <c>.re</c>'s curves: each control by name (one the file hasn't got is left at 0, and a name
    /// the face hasn't got is noted), its length from the file, each control that stays put kept as one value.
    /// </summary>
    public static (LipSyncFile LipSync, List<string> Unknown) FromRe(Hdf5Group root)
    {
        var curves = root.Group(".re_curve_node") ?? throw new InvalidDataException(L.T("LipSyncRe.NoCurves"));
        var data = curves.Dataset("trackdata")?.Data ?? throw new InvalidDataException(L.T("LipSyncRe.NoCurves"));
        if (data.Type.Members is not { } members) throw new InvalidDataException(L.T("LipSyncRe.NoCurves"));
        int frames = data.Count;
        float length = curves.Attributes.TryGetValue(".curvelen", out var len) ? (float)len.Single : (frames - 1) / ReFramesPerSecond;
        var controls = new float[ControlNames.Length][];
        for (int i = 0; i < controls.Length; i++) controls[i] = [0];
        var unknown = new List<string>();
        foreach (var member in members)
        {
            int index = Array.IndexOf(ControlNames, member.Name);
            if (index < 0) { unknown.Add(member.Name); continue; }
            controls[index] = data.Field(member.Name).Select(v => (float)v).ToArray();
        }
        var lipSync = new LipSyncFile { Duration = length, FrameCount = frames, Controls = controls };
        return (lipSync.WithStillControls(), unknown);
    }

    /// <summary>
    /// A lip sync as a <c>.re</c> of REDkit's: every frame of each of the face's 148 controls (a control the game keeps
    /// fewer keys of filled in between them), and the neck and head still.
    /// </summary>
    public static Hdf5Group ToRe(LipSyncFile lipSync)
    {
        int frames = Math.Max(1, lipSync.FrameCount);
        var root = new Hdf5Group("");
        static void Mark(Hdf5Group g, sbyte mark) => g.Attributes[g.Name] = Hdf5Data.Scalar(mark, new Hdf5Type(Hdf5Class.Integer, 1, Signed: true));

        var asset = root.Make(".re_asset_node");
        Mark(asset, AssetMark);
        var container = asset.Make(".re_animation_container");
        Mark(container, AssetMark);
        var animation = container.Make(".re_animation");
        Mark(animation, AssetMark);
        animation.Attributes[".numbones"] = Hdf5Data.Scalar(2, Hdf5Type.Int32);
        animation.Attributes[".numkeys"] = Hdf5Data.Scalar(frames, Hdf5Type.Int32);
        animation.Attributes[".animlength"] = Hdf5Data.Scalar(lipSync.Duration, Hdf5Type.Float32);
        var clip = animation.Make(".re_anim_clip");
        Mark(clip, AssetMark);
        var transform = Hdf5Type.FloatRow(["pos.x", "pos.y", "pos.z", "rot.x", "rot.y", "rot.z", "rot.w", "scale.x", "scale.y", "scale.z"]);
        double[] still = [0, 0, 0, 0, 0, 0, 1, 1, 1, 1];
        foreach (string bone in new[] { "neck", "head" })
        {
            var g = clip.Make(bone);
            Mark(g, AssetMark);
            var rows = Hdf5Data.Rows(transform, Enumerable.Repeat((IReadOnlyList<double>)still, frames).ToList());
            g.Datasets.Add(new Hdf5Dataset("xformframes", new Hdf5Data(transform, [frames, 1], rows.Raw)));
        }
        var description = asset.Make(".re_asset_description_node");
        Mark(description, AssetMark);
        var skeleton = description.Make(".re_skeleton");
        Mark(skeleton, AssetMark);
        skeleton.Datasets.Add(new Hdf5Dataset("bonenames", Hdf5Data.Texts(["neck", "head"], 2, 1)));
        double[] rest = [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1];
        var neck = skeleton.Make("neck");
        Mark(neck, AssetMark);
        neck.Datasets.Add(new Hdf5Dataset(".transform", Hdf5Data.Array(rest, Hdf5Type.Float32, 1, 12)));
        var head = neck.Make("head");
        Mark(head, AssetMark);
        head.Datasets.Add(new Hdf5Dataset(".transform", Hdf5Data.Array(rest, Hdf5Type.Float32, 1, 12)));

        var curves = root.Make(".re_curve_node");
        Mark(curves, CurveMark);
        int count = ControlNames.Length;
        curves.Attributes[".numchannels"] = Hdf5Data.Scalar(count, Hdf5Type.Int32);
        curves.Attributes[".numkeys"] = Hdf5Data.Scalar(frames, Hdf5Type.Int32);
        curves.Attributes[".curvelen"] = Hdf5Data.Scalar(lipSync.Duration, Hdf5Type.Float32);
        var names = ControlNames.ToList();
        var rowsByFrame = new List<IReadOnlyList<double>>(frames);
        var flat = new List<double>(frames * count);
        for (int f = 0; f < frames; f++)
        {
            var row = new double[count];
            for (int c = 0; c < count; c++) row[c] = c < lipSync.Controls.Length ? lipSync.Value(c, f) : 0;
            rowsByFrame.Add(row);
            flat.AddRange(row);
        }
        curves.Datasets.Add(new Hdf5Dataset("curvekeys", Hdf5Data.Array(flat, Hdf5Type.Float32, 1, flat.Count)));
        var trackType = Hdf5Type.FloatRow(names);
        var track = Hdf5Data.Rows(trackType, rowsByFrame);
        curves.Datasets.Add(new Hdf5Dataset("trackdata", new Hdf5Data(trackType, [frames, 1], track.Raw)));
        curves.Datasets.Add(new Hdf5Dataset("tracknames", Hdf5Data.Texts(names, count, 1)));

        var node = root.Make(".re_node");
        node.Attributes[".nodeType"] = Hdf5Data.Scalar(NodeType, new Hdf5Type(Hdf5Class.Integer, 1, Signed: true));
        return root;
    }
}
