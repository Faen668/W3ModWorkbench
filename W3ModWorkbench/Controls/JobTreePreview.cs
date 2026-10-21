using System.IO;
using System.Windows;
using System.Windows.Controls;
using W3ModWorkbench.Core.BehTrees;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Trees;

namespace W3ModWorkbench;

/// <summary>
/// A job tree's preview, as REDkit's job tree editor has one: a character playing the animation of the action picked
/// (from its job category's animation set, on a man, a woman, a child or a dwarf as the category is for, or the
/// character picked), and the category's animations listed beside it, any of them played with a click.
/// </summary>
public sealed class JobTreePreview : Grid, ITreePreview
{
    private readonly ResourceLocator _locator;
    private readonly AnimationPreview _preview = new();
    private readonly ComboBox _entityBox = new() { MinWidth = 160, ToolTip = "The character the animations play on" };
    private readonly ComboBox _categoryBox = new() { ToolTip = "The job category whose animation set is listed" };
    private readonly TextBox _filter = new() { Margin = new Thickness(0, 6, 0, 6), ToolTip = "Show only the animations with these letters" };
    private readonly ListBox _animations = new();
    private readonly TextBlock _setText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) };
    private List<(string Category, string Animset)> _categories = [];
    private List<string> _names = [];
    private string? _animset, _entity;
    private (string Set, string Animation)? _played;
    private bool _entityPicked, _syncing;

    private static readonly string[] EntityNames = ["Man", "Woman", "Child", "Dwarf"];

    public FrameworkElement View => this;

    public JobTreePreview(ResourceLocator locator)
    {
        _locator = locator;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        top.Children.Add(new TextBlock { Text = "Preview on", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75, Margin = new Thickness(0, 0, 8, 0) });
        _entityBox.ItemsSource = EntityNames;
        _entityBox.SelectionChanged += (_, _) =>
        {
            if (_syncing || _entityBox.SelectedIndex < 0) return;
            _entityPicked = true;
            _ = ShowEntityAsync(JobTreeFile.PreviewEntities[_entityBox.SelectedIndex]);
        };
        top.Children.Add(_entityBox);
        Children.Add(top);

        SetRow(_preview, 1);
        Children.Add(_preview);

        var side = new DockPanel { Margin = new Thickness(8, 0, 0, 0) };
        SetColumn(side, 1);
        SetRowSpan(side, 2);
        DockPanel.SetDock(_categoryBox, Dock.Top);
        DockPanel.SetDock(_setText, Dock.Top);
        DockPanel.SetDock(_filter, Dock.Top);
        side.Children.Add(_categoryBox);
        side.Children.Add(_setText);
        side.Children.Add(_filter);
        side.Children.Add(_animations);
        Children.Add(side);
        _categoryBox.SelectionChanged += (_, _) =>
        {
            if (_syncing || _categoryBox.SelectedIndex < 0) return;
            _ = ShowCategoryAsync(_categories[_categoryBox.SelectedIndex].Category, null);
        };
        _filter.TextChanged += (_, _) => ShowNames();
        _animations.SelectionChanged += (_, _) =>
        {
            if (_syncing || _animations.SelectedItem is not string name || _animset is null) return;
            _ = PlayAsync(_animset, name);
        };
        Loaded += async (_, _) =>
        {
            _categories = await Task.Run(() => JobTreeFile.Categories(_locator.ReadFile));
            _syncing = true;
            _categoryBox.ItemsSource = _categories.Select(c => c.Category).ToList();
            _syncing = false;
        };
    }

    /// <summary>Plays the action picked, or a node's first action, on a character for its category.</summary>
    public void Picked(BehTreeNode node)
    {
        var action = node.All().FirstOrDefault(n => n.Object.ClassName != "CJobTreeNode");
        string? category = action?.Object["categoryName"] as string is { Length: > 0 } c ? c
            : node.Object.Array("validCategories").OfType<string>().FirstOrDefault();
        string? anim = action?.Object["animName"] as string is { Length: > 0 } a && a != "None" ? a : null;
        if (category is null) return;
        _ = ShowCategoryAsync(category, anim);
    }

    private async Task ShowCategoryAsync(string category, string? anim)
    {
        if (!_entityPicked)
        {
            string entity = JobTreeFile.PreviewEntity(category);
            _syncing = true;
            _entityBox.SelectedIndex = Array.IndexOf(JobTreeFile.PreviewEntities, entity);
            _syncing = false;
            if (_entity != entity) await ShowEntityAsync(entity);
        }
        var set = await Task.Run(() => JobTreeFile.AnimsetOf(category, _locator.ReadFile));
        _syncing = true;
        _categoryBox.SelectedIndex = _categories.FindIndex(c => c.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        _syncing = false;
        if (set is null)
        {
            _setText.Text = $"No animation set is listed for {category}.";
            _names = [];
            ShowNames();
            return;
        }
        if (!string.Equals(set, _animset, StringComparison.OrdinalIgnoreCase))
        {
            _animset = set;
            _setText.Text = set;
            _names = await Task.Run(() =>
            {
                try
                {
                    if (_locator.ReadFile(set) is not { } data) return [];
                    var file = Cr2wFile.Read(data);
                    return file.Exports.Where(x => x.ClassName == "CSkeletalAnimation").Select(x => file.Object(x.Index)["name"] as string ?? "")
                        .Where(n => n.Length > 0).Order(StringComparer.OrdinalIgnoreCase).ToList();
                }
                catch (InvalidDataException) { return new List<string>(); }
            });
            ShowNames();
        }
        if (anim is null) return;
        _syncing = true;
        _animations.SelectedItem = _names.Contains(anim) ? anim : null;
        if (_animations.SelectedItem is not null) _animations.ScrollIntoView(anim);
        _syncing = false;
        if (_names.Contains(anim))
        {
            await PlayAsync(set, anim);
            return;
        }
        // not in its category's set (the game's copy may leave it out): the other categories' sets are looked in
        var other = await Task.Run(() => _categories.Select(c => c.Animset).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(s => !s.Equals(set, StringComparison.OrdinalIgnoreCase)).FirstOrDefault(s =>
            {
                try { return _locator.ReadFile(s) is { } data && Cr2wFile.Read(data) is var f && f.Exports.Any(x => x.ClassName == "CSkeletalAnimation" && f.Object(x.Index)["name"] as string == anim); }
                catch (InvalidDataException) { return false; }
            }));
        if (other is null)
        {
            _preview.StopAnimation();
            _preview.Message = $"{anim} isn't in {Path.GetFileName(set)}, nor another job category's set.";
            return;
        }
        if (await PlayAsync(other, anim)) _setText.Text = $"{set}\n{anim} is in {other}";
    }

    private async Task ShowEntityAsync(string entity)
    {
        _entity = entity;
        await _preview.LoadEntityAsync(entity, _locator);
        // what was playing plays on the new character
        if (_played is var (set, name)) await _preview.PlayAsync(set, name);
    }

    /// <summary>Plays an animation, kept to play again on another character picked.</summary>
    private Task<bool> PlayAsync(string set, string animation)
    {
        _played = (set, animation);
        return _preview.PlayAsync(set, animation);
    }

    private void ShowNames()
    {
        string text = _filter.Text.Trim();
        _syncing = true;
        var selected = _animations.SelectedItem;
        _animations.ItemsSource = text.Length == 0 ? _names : _names.Where(n => n.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        _animations.SelectedItem = selected;
        _syncing = false;
    }
}
