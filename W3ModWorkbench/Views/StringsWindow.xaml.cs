using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Projects;
using W3ModWorkbench.Core.Strings;

namespace W3ModWorkbench;

/// <summary>Where a row's text stands once an old translation is loaded over the strings.</summary>
public enum TranslationState
{
    /// <summary>No translation loaded.</summary>
    None,

    /// <summary>Found in the old translation, and its text taken from there.</summary>
    Kept,

    /// <summary>Not in the old translation: new in this version, still in the original's language.</summary>
    New,

    /// <summary>Was new, and its text has been changed since.</summary>
    Translated,
}

/// <summary>
/// A string in the editor, as typed: the ID and hash stay text until they're saved, so a half-typed value
/// can be shown and marked rather than refused.
/// </summary>
public sealed class StringRow : INotifyPropertyChanged
{
    private string _id = "";
    private string _keyName = "";
    private string _hash = "";
    private string _text = "";
    private string? _problem;
    private string? _original;
    private TranslationState _state;

    public string IdText { get => _id; set => Set(ref _id, value.Trim()); }

    /// <summary>The key scripts look the string up by. Typing one sets the hash from it.</summary>
    public string KeyName
    {
        get => _keyName;
        set
        {
            string name = value.Trim();
            if (name == _keyName) return;
            Set(ref _keyName, name);
            if (name.Length > 0) Set(ref _hash, W3Strings.HashKey(name).ToString("x8"), nameof(HashText));
        }
    }

    /// <summary>The key's hash in hex: all a file keeps of a key. Typed in only when the key's name isn't known.</summary>
    public string HashText
    {
        get => _hash;
        set
        {
            string hash = value.Trim().ToLowerInvariant();
            if (hash.StartsWith("0x")) hash = hash[2..];
            if (hash == _hash) return;
            Set(ref _hash, hash);
            // a hash that isn't the name's any more means the name is wrong
            if (_keyName.Length > 0 && W3Strings.HashKey(_keyName).ToString("x8") != hash) Set(ref _keyName, "", nameof(KeyName));
        }
    }

    /// <summary>The text. Changing a new string's text counts it as translated.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (value == _text) return;
            Set(ref _text, value);
            if (_state == TranslationState.New) State = TranslationState.Translated;
        }
    }

    /// <summary>The text the file had before a translation was loaded over it, to translate from.</summary>
    public string? Original
    {
        get => _original;
        set { if (_original != value) { _original = value; OnChanged(nameof(Original)); } }
    }

    public TranslationState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnChanged(nameof(State));
            OnChanged(nameof(StateTip));
        }
    }

    public string? StateTip => _state switch
    {
        TranslationState.Kept => "Taken from the old translation",
        TranslationState.New => "Not in the old translation: new in this version, and still needs translating",
        TranslationState.Translated => "New in this version, and translated since",
        _ => null,
    };

    public string? Problem
    {
        get => _problem;
        set
        {
            if (_problem == value) return;
            _problem = value;
            OnChanged(nameof(Problem));
            OnChanged(nameof(HasProblem));
        }
    }

    public bool HasProblem => _problem is not null;

    public bool TryId(out uint id) => uint.TryParse(_id, NumberStyles.None, CultureInfo.InvariantCulture, out id);

    public bool TryHash(out uint hash)
    {
        hash = 0;
        return _hash.Length == 0 || uint.TryParse(_hash, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash);
    }

    public static StringRow From(W3String s) => new()
    {
        _id = s.Id.ToString(CultureInfo.InvariantCulture),
        _keyName = s.KeyName ?? "",
        _hash = s.KeyHash == 0 ? "" : s.KeyHash.ToString("x8"),
        _text = s.Text,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Something the user can change changed: the window marks the file as edited.</summary>
    public event Action? Edited;

    private void Set(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        if (field == value) return;
        field = value;
        OnChanged(name);
        Edited?.Invoke();
    }

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Opens a .w3strings file, or a strings CSV, as a table to read and edit, and saves it as a .w3strings file
/// the game reads, or as a CSV. Key names aren't kept in a .w3strings file, only their hashes; a CSV beside the
/// file gives them back.
/// </summary>
public partial class StringsWindow : Window, IMessageHost
{
    /// <summary>The languages a mod's strings are copied to, so every language shows them.</summary>
    private static readonly string[] CopyLanguages = ["ar", "br", "cz", "de", "en", "es", "esmx", "fr", "hu", "it", "jp", "kr", "pl", "ru", "zh"];

    private readonly List<StringRow> _rows = [];
    private readonly ICollectionView _view;
    private string? _path;          // the .w3strings file Save writes to, once there is one
    private string? _sourcePath;    // what was opened, for the title and where dialogs start
    private uint _version = W3Strings.DefaultVersion;
    private bool _edited;
    private bool _closeConfirmed;

    private StringsWindow()
    {
        InitializeComponent();
        DisabledLook.Apply(this);
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = Matches;
        Grid.ItemsSource = _view;
        LanguageBox.ItemsSource = W3Strings.Languages;
        Grid.SelectionChanged += (_, _) => DeleteButton.IsEnabled = Grid.SelectedItems.Count > 0;
        DeleteButton.IsEnabled = false;
    }

    /// <summary>Opens a .w3strings file or a strings CSV in a window of its own.</summary>
    public static void Open(string path, Window owner)
    {
        var window = new StringsWindow().Standalone(owner);
        try
        {
            window.Load(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            window.Close();
            if (owner is IMessageHost host) _ = host.ShowMessage($"{Path.GetFileName(path)} could not be opened", ex.Message, MessageKind.Error);
            return;
        }
        window.Show();
    }

    /// <summary>Whether a file is something this window opens: a .w3strings file, or a CSV that starts like a strings CSV.</summary>
    public static bool CanOpen(string path)
    {
        if (path.EndsWith(".w3strings", StringComparison.OrdinalIgnoreCase)) return true;
        if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var reader = new StreamReader(path);
            for (int i = 0; i < 5 && reader.ReadLine() is { } line; i++)
                if (line.StartsWith(";meta[language=", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (IOException)
        {
        }
        return false;
    }

    // ---------------------------------------------------------------- loading

    private void Load(string path)
    {
        _sourcePath = path;
        W3Language? language;
        List<W3String> strings;
        var notes = new List<string>();
        if (path.EndsWith(".w3strings", StringComparison.OrdinalIgnoreCase))
        {
            var file = W3Strings.Read(File.ReadAllBytes(path), path, KnownKeys(path));
            (language, strings, _version, _path) = (file.Language, file.Strings, file.Version, path);
            int unnamed = strings.Count(s => s.KeyHash != 0 && s.KeyName is null);
            if (unnamed > 0)
                notes.Add($"{Format.Count(unnamed, "key")} shown by hash: a .w3strings file keeps only that. A strings CSV in the same folder gives the names back.");
        }
        else
        {
            var csv = W3Strings.FromCsv(File.ReadAllText(path, Encoding.UTF8));
            (language, strings) = (csv.Language ?? W3Strings.LanguageFromName(path), csv.Strings);
            notes.AddRange(csv.Problems.Take(3).Select(p => $"Line {p.Line} was skipped: {p.Message}"));
            if (csv.Problems.Count > 3) notes.Add($"…and {csv.Problems.Count - 3} more lines.");
        }

        foreach (var s in strings) Add(StringRow.From(s));
        _view.Refresh();
        LanguageBox.SelectedItem = language ?? W3Strings.Language("en");
        Validate();
        _edited = false;
        UpdateHeader();
        if (notes.Count > 0) Loaded += (_, _) => _ = ShowMessage("Opened with notes", string.Join("\n\n", notes), MessageKind.Info);
    }

    /// <summary>
    /// Key names by hash, from the strings CSVs in the file's folder, which is where mods keep theirs: a
    /// .w3strings file on its own only has the hashes.
    /// </summary>
    private static Dictionary<uint, string> KnownKeys(string path)
    {
        var names = new Dictionary<uint, string>();
        try
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            foreach (string csv in Directory.EnumerateFiles(folder, "*.csv").Take(20))
            {
                if (!CanOpen(csv) || new FileInfo(csv).Length > 64 * 1024 * 1024) continue;
                foreach (var s in W3Strings.FromCsv(File.ReadAllText(csv, Encoding.UTF8)).Strings)
                    if (s.KeyName is not null) names.TryAdd(s.KeyHash, s.KeyName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // no names, just hashes
        }
        return names;
    }

    // ---------------------------------------------------------------- loading a translation over the strings

    private bool _translationLoaded;

    private void LoadTranslation_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load an old translation over these strings",
            Filter = "Strings (*.csv;*.w3strings)|*.csv;*.w3strings|Strings CSV (*.csv)|*.csv|Witcher 3 strings (*.w3strings)|*.w3strings",
            InitialDirectory = Path.GetDirectoryName(_path ?? _sourcePath) ?? "",
        };
        if (dialog.ShowDialog(this) == true) LoadTranslation(dialog.FileName);
    }

    /// <summary>
    /// For a mod's update: takes the text of each string from an older translation, matched by key, or by ID for
    /// strings without one. The text there was is kept as the original to translate from, and strings the old
    /// translation doesn't have are marked new, so only those are left to do.
    /// </summary>
    private void LoadTranslation(string path)
    {
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        W3Language? language;
        List<W3String> old;
        int skipped = 0;
        try
        {
            if (path.EndsWith(".w3strings", StringComparison.OrdinalIgnoreCase))
            {
                // only key hashes in here, but they match just as well
                var file = W3Strings.Read(File.ReadAllBytes(path), path);
                (language, old) = (file.Language, file.Strings);
            }
            else
            {
                var csv = W3Strings.FromCsv(File.ReadAllText(path, Encoding.UTF8));
                (language, old, skipped) = (csv.Language ?? W3Strings.LanguageFromName(path), csv.Strings, csv.Problems.Count);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _ = ShowMessage($"{Path.GetFileName(path)} could not be read", ex.Message, MessageKind.Error);
            return;
        }

        var byHash = new Dictionary<uint, W3String>();
        var byId = new Dictionary<uint, W3String>();
        foreach (var s in old)
        {
            if (s.KeyHash != 0) byHash.TryAdd(s.KeyHash, s);
            byId.TryAdd(s.Id, s);
        }

        var used = new HashSet<W3String>(ReferenceEqualityComparer.Instance);
        int kept = 0, fresh = 0;
        foreach (var row in _rows)
        {
            // loading another translation starts again from the original, without counting that as translating,
            // apart from strings translated here, which are kept when it doesn't have them either
            var before = row.State;
            row.State = TranslationState.None;
            row.Original ??= row.Text;
            row.TryId(out uint id);
            bool keyed = row.TryHash(out uint hash) && hash != 0;
            // by key when both have one, which survives a string's ID changing; by ID when either has none
            W3String? match = keyed && byHash.TryGetValue(hash, out var k) ? k
                : byId.TryGetValue(id, out var i) && (!keyed || i.KeyHash == 0) ? i
                : null;
            if (match is not null)
            {
                row.Text = match.Text;
                row.State = TranslationState.Kept;
                used.Add(match);
                kept++;
            }
            else if (before == TranslationState.Translated)
                row.State = TranslationState.Translated;
            else
            {
                row.Text = row.Original;
                row.State = TranslationState.New;
                fresh++;
            }
        }

        _translationLoaded = true;
        OriginalColumn.Visibility = Visibility.Visible;
        StateColumn.Visibility = Visibility.Visible;
        ShowBox.Visibility = Visibility.Visible;
        if (language is not null && !Equals(LanguageBox.SelectedItem, language)) LanguageBox.SelectedItem = language;
        ShowBox.SelectedIndex = fresh > 0 ? 1 : 0;
        OnEdited();
        _view.Refresh();
        Validate();

        int unused = old.Count - used.Count;
        var lines = new List<string>
        {
            $"{Format.Count(kept, "string")} took {(kept == 1 ? "its" : "their")} text from {Path.GetFileName(path)}.",
            fresh == 0 ? "Every string was found, so there's nothing new to translate."
                : $"{Format.Count(fresh, "string")} {(fresh == 1 ? "isn't" : "aren't")} in it: new in this version, marked and shown first, with the original text to translate from.",
        };
        if (unused > 0) lines.Add($"{Format.Count(unused, "string")} in the old translation {(unused == 1 ? "isn't" : "aren't")} in these strings any more, and {(unused == 1 ? "was" : "were")} left out.");
        if (skipped > 0) lines.Add($"{Format.Count(skipped, "line")} of the CSV couldn't be read and {(skipped == 1 ? "was" : "were")} skipped.");
        if (language is not null) lines.Add($"The language is set to {language.Handle}, the translation's.");
        _ = ShowMessage("Translation loaded", string.Join("\n\n", lines), MessageKind.Info);
    }

    // ---------------------------------------------------------------- the game's strings

    private readonly Dictionary<uint, string> _clashes = [];

    private string? GameFolder() => GameLocator.Resolve(_path ?? _sourcePath);

    private void GameStrings_Click(object sender, RoutedEventArgs e)
    {
        GameStringsWindow.Open(this, GameFolder(), StringsLanguage.Handle);
    }

    /// <summary>
    /// Marks the IDs that the game, a DLC or another installed mod already uses, since two strings with one ID
    /// show each other's text. The file being edited is left out when it is itself one of the installed ones.
    /// </summary>
    private async void CheckIds_Click(object sender, RoutedEventArgs e)
    {
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (GameFolder() is not { } folder)
        {
            _ = ShowMessage("The game folder isn't set", "Checking IDs needs The Witcher 3's folder to compare with. Set it in Settings.", MessageKind.Info);
            return;
        }
        _ = Message.Show(null, "Reading the game's strings…", MessageKind.Busy);
        GameStringIndex index;
        try
        {
            index = await GameStringIndex.GetAsync(folder, StringsLanguage);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            await ShowMessage("The game's strings could not be read", ex.Message, MessageKind.Error);
            return;
        }

        string? own = _path is null ? null : Path.GetFullPath(_path);
        _clashes.Clear();
        var examples = new List<string>();
        foreach (var row in _rows)
        {
            if (!row.TryId(out uint id)) continue;
            var others = index.WithId(id)
                .Where(s => own is null || !Path.GetFullPath(Path.Combine(folder, s.File)).Equals(own, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (others.Count == 0) continue;
            var first = others[0];
            string shown = first.Text.ReplaceLineEndings(" ");
            if (shown.Length > 60) shown = shown[..60] + "…";
            _clashes[id] = $"Also used by {first.Source} ({first.File}): \"{shown}\"" + (others.Count > 1 ? $" and {others.Count - 1} more" : "");
            if (examples.Count < 5) examples.Add($"{id}  →  {first.Source}");
        }
        Validate();
        Message.Close(false);

        string free = $"{index.FreeBlock():0}";
        string range = $"{free} to {uint.Parse(free) + 999}";
        string text = _clashes.Count == 0
            ? $"None of these IDs is used by the game, its DLC or the other installed mods.\n\nIf you need a fresh block of IDs, {range} is free."
            : $"{Format.Count(_clashes.Count, "ID")} {(_clashes.Count == 1 ? "is" : "are")} also used elsewhere, and marked with a warning sign:\n\n{string.Join("\n", examples)}"
              + (_clashes.Count > examples.Count ? $"\n…and {_clashes.Count - examples.Count} more." : "")
              + $"\n\nA block nothing uses: {range}.";
        await ShowMessage(_clashes.Count == 0 ? "No clashes" : "IDs in use elsewhere", text,
            _clashes.Count == 0 ? MessageKind.Info : MessageKind.Warning);
    }

    private void ShowBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        _view.Refresh();
        Validate();
    }

    private void Add(StringRow row, int index = -1)
    {
        row.Edited += OnEdited;
        if (index < 0 || index > _rows.Count) _rows.Add(row);
        else _rows.Insert(index, row);
    }

    private void OnEdited()
    {
        if (!_edited)
        {
            _edited = true;
            UpdateHeader();
        }
    }

    private void UpdateHeader()
    {
        string name = Path.GetFileName(_path ?? _sourcePath ?? "Strings");
        Title = $"{(_edited ? "• " : "")}{name} - Strings";
        FileNameText.Text = (_edited ? "• " : "") + name;
        var language = LanguageBox.SelectedItem as W3Language;
        DetailText.Text = $"{language?.Handle ?? "?"}  ·  {Path.GetDirectoryName(_sourcePath) ?? ""}";
        SaveButton.ToolTip = _path is null ? "Save as a .w3strings file (Ctrl+S)" : $"Save to {Path.GetFileName(_path)} (Ctrl+S)";
    }

    // ---------------------------------------------------------------- checking

    /// <summary>
    /// Marks rows that can't be saved (an ID that isn't a number, a hash that isn't hex, an ID used twice) and
    /// counts the ones outside a mod's ID range, which save but can clash with the game's or another mod's.
    /// </summary>
    private int Validate()
    {
        var seen = new Dictionary<uint, int>();
        foreach (var row in _rows)
            if (row.TryId(out uint id)) seen[id] = seen.GetValueOrDefault(id) + 1;
        var keys = new Dictionary<string, int>();
        foreach (var row in _rows.Where(r => r.HashText.Length > 0)) keys[row.HashText] = keys.GetValueOrDefault(row.HashText) + 1;

        int errors = 0, outside = 0, sharedKeys = 0, clashing = 0;
        foreach (var row in _rows)
        {
            string? problem = null;
            if (!row.TryId(out uint id)) problem = "The ID has to be a whole number.";
            else if (seen[id] > 1) problem = "Another string has this ID.";
            else if (!row.TryHash(out _)) problem = "The key hash has to be eight hex digits.";
            if (problem is not null) errors++;
            else if (_clashes.TryGetValue(id, out var clash))
            {
                clashing++;
                problem = clash;
            }
            else if (!W3Strings.InModIdSpace(id))
            {
                outside++;
                problem = "Outside the IDs mods use (211nnnn000 to 211nnnn999), so it may clash with the game's strings or another mod's.";
            }
            if (problem is null && row.HashText.Length > 0 && keys[row.HashText] > 1)
            {
                sharedKeys++;
                problem = "Another string has this key, so scripts will only find one of them.";
            }
            row.Problem = problem;
        }

        int untranslated = _rows.Count(r => r.State == TranslationState.New);
        StatusText.Text = $"{Format.Count(_rows.Count, "string")}  ·  {Format.Count(_rows.Count(r => r.HashText.Length > 0), "key")}"
                          + (_translationLoaded ? $"  ·  {untranslated:N0} to translate" : "")
                          + (SearchBox.Text.Length > 0 || _translationLoaded && ShowBox.SelectedIndex > 0 ? $"  ·  {_view.Cast<object>().Count()} shown" : "");
        var parts = new List<string>();
        if (errors > 0) parts.Add($"{Format.Count(errors, "string")} can't be saved until fixed");
        if (clashing > 0) parts.Add($"{clashing} with an ID the game or another mod uses");
        if (outside > 0) parts.Add($"{outside} outside the mod ID range");
        if (sharedKeys > 0) parts.Add($"{sharedKeys} with a key another string has");
        ProblemText.Text = string.Join("  ·  ", parts);
        return errors;
    }

    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) =>
        // the edit reaches the row after this event, so check once it has
        Dispatcher.BeginInvoke(() => Validate(), System.Windows.Threading.DispatcherPriority.Background);

    // ---------------------------------------------------------------- searching

    private bool Matches(object item)
    {
        if (item is not StringRow row) return true;
        if (_translationLoaded && ShowBox.SelectedIndex > 0 && row.State != ShownState) return false;
        string query = SearchBox?.Text.Trim() ?? "";
        if (query.Length == 0) return true;
        return row.IdText.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.KeyName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.HashText.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Original?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>The rows the Show list picks, in its order after All.</summary>
    private TranslationState ShownState => ShowBox.SelectedIndex switch
    {
        1 => TranslationState.New,
        2 => TranslationState.Translated,
        _ => TranslationState.Kept,
    };

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        Validate();
    }

    // ---------------------------------------------------------------- editing

    /// <summary>A new string after the selected one, with the next ID after the highest in its thousand.</summary>
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        var after = Grid.SelectedItem as StringRow ?? _rows.LastOrDefault();
        uint next = 2110000000;
        if (after is not null && after.TryId(out uint id))
        {
            uint block = id / 1000 * 1000;
            next = _rows.Select(r => r.TryId(out uint v) ? v : 0).Where(v => v / 1000 * 1000 == block).DefaultIfEmpty(id).Max() + 1;
        }
        var row = new StringRow { IdText = next.ToString(CultureInfo.InvariantCulture) };
        Add(row, after is null ? -1 : _rows.IndexOf(after) + 1);
        _view.Refresh();
        OnEdited();
        Validate();
        Grid.SelectedItem = row;
        Grid.ScrollIntoView(row);
        Grid.CurrentCell = new DataGridCellInfo(row, Grid.Columns[2]);
        Grid.Focus();
        Grid.BeginEdit();
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        var selected = Grid.SelectedItems.OfType<StringRow>().ToList();
        if (selected.Count == 0) return;
        Grid.CancelEdit();
        foreach (var row in selected) _rows.Remove(row);
        _view.Refresh();
        OnEdited();
        Validate();
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        OnEdited();
        UpdateHeader();
    }

    // ---------------------------------------------------------------- saving

    private List<W3String>? Strings()
    {
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (Validate() > 0)
        {
            _ = ShowMessage("Some strings can't be saved yet", "The rows with a warning sign have an ID that isn't a number, an ID another string has, "
                + "or a key hash that isn't hex. Rest the mouse on the sign to see which.", MessageKind.Warning);
            return null;
        }
        return [.. _rows.Select(r =>
        {
            r.TryId(out uint id);
            r.TryHash(out uint hash);
            return new W3String(id, hash, r.KeyName.Length > 0 ? r.KeyName : null, r.Text);
        })];
    }

    private W3Language StringsLanguage => LanguageBox.SelectedItem as W3Language ?? W3Strings.Languages[0];

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(_path);

    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsync(null);

    /// <summary>Writes the .w3strings file, asking where when <paramref name="path"/> is null, and the copies for every language when asked for.</summary>
    private async Task<bool> SaveAsync(string? path)
    {
        if (Strings() is not { } strings) return false;
        // the game's own strings, and an installed mod's, are never saved over: they're saved somewhere of your own
        if (path is not null && FileSafety.Protection(path) is { } protection)
        {
            await ShowMessage($"{Path.GetFileName(path)} is read-only",
                protection + " Save it into your project, or anywhere of your own, from the next window.", MessageKind.Info);
            path = null;
        }
        if (path is null)
        {
            string near = Path.GetDirectoryName(_path ?? _sourcePath) ?? "";
            var dialog = new SaveFileDialog
            {
                Title = "Save strings",
                Filter = "Witcher 3 strings (*.w3strings)|*.w3strings",
                FileName = $"{StringsLanguage.Handle}.w3strings",
                InitialDirectory = FileSafety.CanEdit(Path.Combine(near, "_")) ? near : ProjectWorkspace.Current?.Root ?? "",
            };
            if (dialog.ShowDialog(this) != true) return false;
            path = dialog.FileName;
        }

        string folder = Path.GetDirectoryName(path)!;
        var copies = AllLanguagesCheck.IsChecked == true
            ? CopyLanguages.Select(l => Path.Combine(folder, l + ".w3strings")).Where(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        int replacing = copies.Count(File.Exists);
        if (replacing > 0 && !await Message.Show("Replace the other languages' files?",
                $"{Format.Count(replacing, "file")} in {folder} will be replaced with a copy of these strings.",
                MessageKind.Question, primary: "Replace", secondary: "Cancel"))
            return false;

        try
        {
            byte[] data = W3Strings.Write(strings, StringsLanguage, _version);
            FileSafety.CheckWrite(path);
            foreach (string copy in copies) FileSafety.CheckWrite(copy);
            File.WriteAllBytes(path, data);
            foreach (string copy in copies) File.WriteAllBytes(copy, data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ShowMessage("The strings could not be saved", ex.Message, MessageKind.Error);
            return false;
        }
        _path = path;
        _edited = false;
        UpdateHeader();
        string savedText = copies.Count > 0
            ? $"Saved {Path.GetFileName(path)} and {copies.Count} {(copies.Count == 1 ? "copy" : "copies")} for the other languages."
            : $"Saved {Path.GetFileName(path)}.";
        if (ProjectPrompts.ShouldOffer(path)) _ = ProjectPrompts.OfferAsync(path, Message, savedText);
        else _ = Message.Show(null, savedText, MessageKind.Info, autoClose: true);
        return true;
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (Strings() is not { } strings) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export strings as CSV",
            Filter = "Strings CSV (*.csv)|*.csv",
            FileName = $"{StringsLanguage.Handle}.csv",
            InitialDirectory = Path.GetDirectoryName(_path ?? _sourcePath) ?? "",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            FileSafety.CheckWrite(dialog.FileName);
            File.WriteAllText(dialog.FileName, W3Strings.ToCsv(new W3StringsFile(StringsLanguage, _version, strings)), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ShowMessage("The CSV could not be saved", ex.Message, MessageKind.Error);
            return;
        }
        _ = Message.Show(null, $"Exported {Path.GetFileName(dialog.FileName)}.", MessageKind.Info, autoClose: true);
    }

    // ---------------------------------------------------------------- window

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_edited || _closeConfirmed) return;
        e.Cancel = true;
        if (await Message.Show("Close without saving?", "Your changes to these strings haven't been saved.",
                MessageKind.Question, primary: "Close without saving", secondary: "Keep editing"))
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen) return;
        var mods = Keyboard.Modifiers;
        bool editing = Keyboard.FocusedElement is TextBox box && box != SearchBox;
        if (e.Key == Key.S && mods == ModifierKeys.Control) _ = SaveAsync(_path);
        else if (e.Key == Key.S && mods == (ModifierKeys.Control | ModifierKeys.Shift)) _ = SaveAsync(null);
        else if (e.Key == Key.F && mods == ModifierKeys.Control) SearchBox.Focus();
        else if (e.Key == Key.N && mods == ModifierKeys.Control) Add_Click(sender, e);
        else if (e.Key == Key.Delete && mods == ModifierKeys.None && !editing && Grid.IsKeyboardFocusWithin) DeleteSelected();
        else if (e.Key == Key.Escape && !editing)
        {
            if (SearchBox.IsKeyboardFocused && SearchBox.Text.Length > 0) SearchBox.Clear();
            else Close();
        }
        else return;
        e.Handled = true;
    }

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: "OK");
}
