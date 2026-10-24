using System.ComponentModel;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using W3ModWorkbench.Core.Effects;
using W3ModWorkbench.Core.Entities;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>An effect in the Effects tab: its name, what it's made of in a line, and whether it's playing in the 3D view.</summary>
public sealed class EffectRow(EntityEffect effect, string summary, Action<EffectRow> switched) : INotifyPropertyChanged
{
    private bool _on;

    public EntityEffect Effect { get; } = effect;
    public string Name => Effect.Name;
    public string Summary { get; } = summary;

    public bool On
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(On)));
            switched(this);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A part of the effect picked: when (seconds), what kind, and what it uses; dimmed when the 3D view doesn't draw it.</summary>
public sealed record EffectPartRow(string Time, string Kind, string Detail, bool Drawn, string Tip)
{
    public double Opacity => Drawn ? 1 : 0.55;
}

public partial class EntityWindow
{
    private List<EntityEffect> _effects = [];
    private EffectPlayer? _effectPlayer;
    private readonly System.Diagnostics.Stopwatch _effectsClock = new();
    private double _effectsLast, _effectsStatusAt;
    private bool _effectsTicking;

    // the bones where they are now (the animation's pose, or the rig as it stands), in the entity's space, by name
    private Matrix4x4[]? _bones;
    private string[]? _boneNames;

    private EffectPlayer Effects => _effectPlayer ??= new EffectPlayer(_locator, EffectPlaces);

    /// <summary>Lists the entity's effects, keeping on the ones that were playing (by name), as when REDkit's copy is read after the game's.</summary>
    private void ShowEffects(List<EntityEffect> effects)
    {
        var playing = Effects.Playing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Effects.StopAll();
        _effects = effects;
        var rows = effects.Select(e => new EffectRow(e, EffectSummary(e), EffectSwitched)).ToList();
        EffectList.ItemsSource = rows;
        EffectSwitches.ItemsSource = rows;
        EffectsButton.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var row in rows.Where(r => playing.Contains(r.Name))) row.On = true;
        EffectList.SelectedIndex = rows.Count > 0 ? 0 : -1;
        UpdateEffects();
    }

    /// <summary>An effect in a line: whether it loops, how long it is, and its parts by kind.</summary>
    private static string EffectSummary(EntityEffect e)
    {
        var parts = new List<string> { e.Looped ? L.T("EntityWindow.EffectLooped") : L.T("EntityWindow.EffectOnce"), L.T("EntityWindow.EffectLength", e.Length) };
        foreach (var group in e.Parts.GroupBy(p => p.Kind).OrderBy(g => g.Key))
            parts.Add(L.T(group.Key switch
            {
                EffectPartKind.Particles => "Count.EffectParticles",
                EffectPartKind.Sound => "Count.EffectSound",
                EffectPartKind.Light => "Count.EffectLight",
                EffectPartKind.MaterialOverride => "Count.EffectMaterial",
                EffectPartKind.Flare => "Count.EffectFlare",
                EffectPartKind.Parameter => "Count.EffectParameter",
                EffectPartKind.Screen => "Count.EffectScreen",
                _ => "Count.EffectOther",
            }, group.Count()));
        return string.Join("  ·  ", parts);
    }

    private static string KindName(EffectPartKind kind) => L.T(kind switch
    {
        EffectPartKind.Particles => "EntityWindow.PartParticles",
        EffectPartKind.Sound => "EntityWindow.PartSound",
        EffectPartKind.Light => "EntityWindow.PartLight",
        EffectPartKind.MaterialOverride => "EntityWindow.PartMaterial",
        EffectPartKind.Flare => "EntityWindow.PartFlare",
        EffectPartKind.Parameter => "EntityWindow.PartParameter",
        EffectPartKind.Screen => "EntityWindow.PartScreen",
        _ => "EntityWindow.PartOther",
    });

    private void EffectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EffectList.SelectedItem is not EffectRow row)
        {
            EffectPartsTitle.Visibility = EffectParts.Visibility = Visibility.Collapsed;
            return;
        }
        EffectPartsTitle.Text = L.T("EntityWindow.EffectParts", row.Name, row.Effect.FromRedkit ? L.T("EntityWindow.EffectFromRedkit") : L.T("EntityWindow.EffectFromGame"));
        EffectParts.ItemsSource = row.Effect.Parts.Select(PartRow).ToList();
        EffectPartsTitle.Visibility = EffectParts.Visibility = Visibility.Visible;
    }

    private static EffectPartRow PartRow(EffectPart p)
    {
        bool drawn = p.Kind == EffectPartKind.Particles && p.ParticleSystem is not null;
        string where = p.Spawner is { } s
            ? string.Join(", ", new[] { s.Component, s.ParentSlot }.Concat(s.Slots).Concat(s.Bones).OfType<string>())
            : p.GroupComponent ?? "";
        string detail = p.Kind switch
        {
            EffectPartKind.Particles => System.IO.Path.GetFileName(p.ParticleSystem ?? ""),
            EffectPartKind.Sound => p.SoundEvent ?? "",
            EffectPartKind.Light => L.T("EntityWindow.PartLightDetail", p.LightRadius, p.LightBrightness),
            _ => p.ClassName.StartsWith("CFXTrackItem", StringComparison.Ordinal) ? p.ClassName[12..] : p.ClassName,
        };
        if (where.Length > 0) detail = L.T("EntityWindow.PartOn", detail, where);
        string tip = (p.ParticleSystem ?? p.ClassName) + (drawn ? "" : "\n" + L.T("EntityWindow.PartNotDrawn"));
        return new EffectPartRow(L.T("EntityWindow.PartTime", p.Start, p.End), KindName(p.Kind), detail, drawn, tip);
    }

    /// <summary>An effect switched on or off: it starts from its beginning, or stops, in the 3D view.</summary>
    private void EffectSwitched(EffectRow row)
    {
        if (row.On) Effects.Start(row.Effect);
        else Effects.Stop(row.Effect);
        UpdateEffects();
    }

    private void EffectsOff_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in EffectList.Items.OfType<EffectRow>()) row.On = false;
    }

    /// <summary>Starts the effects' clock when one plays, stops it and clears the particles when none does, and says how it's going.</summary>
    private void UpdateEffects()
    {
        bool any = _effectPlayer?.Any == true;
        if (any && !_effectsTicking)
        {
            _effectsTicking = true;
            _effectsClock.Restart();
            _effectsLast = 0;
            System.Windows.Media.CompositionTarget.Rendering += EffectsTick;
        }
        else if (!any && _effectsTicking)
        {
            _effectsTicking = false;
            System.Windows.Media.CompositionTarget.Rendering -= EffectsTick;
            View.ShowParticles(null);
        }
        ShowEffectsStatus();
    }

    // the effects play at the animation's speed, whether the animation plays or not
    private void EffectsTick(object? sender, EventArgs e)
    {
        if (_effectPlayer is not { } player) return;
        double now = _effectsClock.Elapsed.TotalSeconds;
        float step = (float)Math.Min(now - _effectsLast, 0.1);
        _effectsLast = now;
        player.Advance(step * _speed);
        View.ShowParticles(player.Batches());
        if (now - _effectsStatusAt > 0.5)
        {
            _effectsStatusAt = now;
            ShowEffectsStatus();
        }
    }

    private void ShowEffectsStatus()
    {
        int playing = _effectPlayer?.Playing.Count() ?? 0;
        EffectsButtonText.Text = playing > 0 ? L.T("EntityWindow.EffectsButtonPlaying", playing, _effects.Count) : L.T("Common.Effects");
        EffectsStatus.Text = EffectsPopupStatus.Text = EffectsStatusText();
    }

    private string EffectsStatusText()
    {
        if (_effects.Count == 0) return L.T("EntityWindow.NoEffects");
        if (!MeshView.CanDrawParticles) return L.T("EntityWindow.CantDrawParticles", W3Shading.ParticleProblem ?? "");
        var player = _effectPlayer;
        var lines = new List<string> { L.T("EntityWindow.EffectsPlaying", player?.Playing.Count() ?? 0, _effects.Count) };
        if (player?.Any == true) lines.Add(L.T("Count.Particle", player.Count) + (player.Count >= EffectPlayer.MaxSprites ? L.T("EntityWindow.ParticlesCapped") : ""));
        if (player?.Missing.ToList() is { Count: > 0 } missing) lines.Add(L.T("EntityWindow.ParticlesMissing", string.Join(", ", missing.Select(System.IO.Path.GetFileName))));
        return string.Join("  ·  ", lines);
    }

    private void StopEffects()
    {
        _effectPlayer?.StopAll();
        UpdateEffects();
    }

    // ---------------------------------------------------------------- where a part goes on the entity

    /// <summary>
    /// Where a part of an effect goes now, in the entity's space: for a simple spawner, each of its slots and bones; for
    /// a component spawner, its component (or the slot of it it names); otherwise the component its track group is for,
    /// or the entity's origin. The spawner's own offset and turn are on top.
    /// </summary>
    private IReadOnlyList<Matrix4x4> EffectPlaces(EffectPart part)
    {
        var s = part.Spawner;
        // the spawner's angles are pitch, yaw and roll
        var offset = s is null ? Matrix4x4.Identity : EntityFile.Rotation(s.Rotation.Z, s.Rotation.X, s.Rotation.Y) * Matrix4x4.CreateTranslation(s.Position);
        var places = new List<Matrix4x4>();
        if (s?.ClassName == "CFXSimpleSpawner") // loc-ignore
        {
            foreach (string slot in s.Slots)
                if ((SlotPlace(slot) ?? BonePlace(slot)) is { } at) places.Add(offset * at);
            foreach (string bone in s.Bones)
                if ((BonePlace(bone) ?? SlotPlace(bone)) is { } at) places.Add(offset * at);
        }
        else if (s?.ClassName == "CFXSpawnerComponent") // loc-ignore
        {
            var at = ComponentPlace(s.Component ?? part.GroupComponent);
            if (s.ParentSlot is { } slot) at = SlotPlace(slot) ?? BonePlace(slot) ?? at;
            places.Add(offset * at);
        }
        if (places.Count == 0) places.Add(offset * ComponentPlace(part.GroupComponent));
        return places;
    }

    private Matrix4x4 ComponentPlace(string? name) =>
        name is not null && _look?.Components.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } component
            ? component.Placed : Matrix4x4.Identity;

    /// <summary>A slot of the entity: where it is on its bone, or else on its component.</summary>
    private Matrix4x4? SlotPlace(string name)
    {
        if (_entity?.Slots.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } slot) return null;
        var on = slot.Bone is { } bone && BonePlace(bone) is { } at ? at : ComponentPlace(slot.Component);
        return slot.Transform * on;
    }

    private Matrix4x4? BonePlace(string name)
    {
        if (_bones is null || _boneNames is null) return null;
        int i = Array.FindIndex(_boneNames, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i < _bones.Length ? _bones[i] : null;
    }
}
