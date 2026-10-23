using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using W3ModWorkbench.Core;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Sounds;
using W3ModWorkbench.Core.Strings;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench;

/// <summary>
/// A sound to play and look into: a streamed sound or a voice line (.wem), or a sound bank (.bnk) with the sounds it
/// plays. Its waveform with a line that follows it (click to go to a moment), play, stop, loop and volume, a voice line's
/// words in any language with who says it, everything the sound says about itself (codec, channels, rate, length, loop
/// and marked points, its parts), which banks play it, and a bank's sounds and objects. Save it as a WAV or as the game
/// keeps it.
/// </summary>
public partial class SoundWindow : Window, IMessageHost
{
    private readonly string _path;
    private readonly string _source;
    private readonly Func<byte[]> _readData;
    private readonly ResourceLocator _locator;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private SoundPlayer? _player;
    private (short[] Samples, int Channels, int Rate)? _decoded;
    private byte[]? _riff;
    private string _soundName = "";
    private uint? _soundId;
    private SoundBank? _bank;
    private (W3Language Language, uint StringId)? _line;
    private int _soundVersion;
    private bool _syncing;

    private sealed record DetailRow(string Name, string Value);

    /// <summary>A sound a bank plays or keeps: kept in it, in another bank (named), or streamed.</summary>
    private sealed record SoundRow(uint Id, BankSound? Sound, bool InBank, string? OtherBank, string Kind, WemInfo? Info)
    {
        public string Kept => InBank ? L.T("SoundWindow.Bank2") : OtherBank is { } other ? L.T("SoundWindow.In2", Path.GetFileNameWithoutExtension(other)) : Info is null ? L.T("Common.NotFound") : L.T("SoundWindow.Streamed");
        public string Codec => Info?.CodecName ?? "";
        public int Channels => Info?.Channels ?? 0;
        public double Length => Info?.Duration ?? 0;
        public string LengthText => Info is { Duration: > 0 } i ? $"{i.Duration:0.00} s" : "";
        public long Size => Info?.Size ?? Sound?.MediaSize ?? 0;
        public string SizeText => Size > 0 ? Format.Bytes(Size) : "";
    }

    private sealed record BankRow(string Bank, string Object, string Kind);

    private SoundWindow(string path, string source, Func<byte[]> readData, ResourceLocator locator)
    {
        InitializeComponent();
        MakeModBtn.Show(path, Path.IsPathRooted(source) ? source : null, readData);
        _path = path;
        _source = source;
        _readData = readData;
        _locator = locator;
        Title = L.T("SoundWindow.Sound2", Path.GetFileName(path));
        InfoText.Text = $"{path}   ·   {source}";
        _timer.Tick += (_, _) => ShowPosition();
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _player?.Dispose();
        };
    }

    public static bool CanShow(string name) =>
        name.EndsWith(".wem", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase);

    public static void Open(string path, string source, byte[] data, ResourceLocator locator, Window owner, IArchive? archive = null) =>
        new SoundWindow(path, source, () => data, locator).Standalone(owner).Show();

    public static void OpenFile(string file, ResourceLocator locator, Window owner) =>
        new SoundWindow(Path.GetFileName(file), file, () => File.ReadAllBytes(file), locator).Standalone(owner).Show();

    /// <summary>Opens a sound event by its name: the sounds it can play, each to play, and what its actions do.</summary>
    public static void OpenEvent(string name, ResourceLocator locator, Window owner) =>
        new SoundWindow(SoundLinks.Event + name, L.T("SoundWindow.SoundEvent"), () => [], locator).Standalone(owner).Show();

    public Task ShowMessage(string? title, string message, MessageKind kind) => Message.Show(title, message, kind, primary: L.T("Common.OK"));

    private async Task LoadAsync()
    {
        if (_path.StartsWith(SoundLinks.Event, StringComparison.Ordinal))
        {
            await ShowEventAsync(_path[SoundLinks.Event.Length..]);
            return;
        }
        byte[] data;
        try
        {
            data = await Task.Run(_readData);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            BusyText.Text = L.T("SoundWindow.SoundCouldNotRead", ex.Message);
            return;
        }
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        VolumeSlider.Value = 0.8;
        if (_path.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase))
        {
            await ShowBankAsync(data);
            return;
        }
        SoundsTab.Visibility = ObjectsTab.Visibility = Visibility.Collapsed;
        uint? id = uint.TryParse(Path.GetFileNameWithoutExtension(_path), out uint parsed) ? parsed : null;
        _line = SpeechArchive.LineOf(_path);
        TitleText.Text = _line is { } line ? L.T("SoundWindow.VoiceLine", line.StringId) : id is { } streamed ? L.T("SoundWindow.Sound3", streamed) : Path.GetFileName(_path);
        await ShowSoundAsync(data, Path.GetFileName(_path), id);
        if (_line is { } voice) _ = ShowLineAsync(voice.Language, voice.StringId);
        else BanksTab.Visibility = Visibility.Visible;
        if (_line is null && id is { } own) _ = ShowBanksAsync(own);
        else BanksTab.Visibility = Visibility.Collapsed;
        Tabs.SelectedItem = DetailsTab;
    }

    // ---------------------------------------------------------------- a sound

    /// <summary>Shows a sound's details and waveform and makes it ready to play, saying why when its codec can't be played.</summary>
    private async Task ShowSoundAsync(byte[] riff, string name, uint? id, string? where = null)
    {
        int version = ++_soundVersion;
        _player?.Dispose();
        _player = null;
        _decoded = null;
        _riff = riff;
        _soundName = name;
        _soundId = id;
        _timer.Stop();
        PlayIcon.Text = "";
        CopyButton.IsEnabled = id is not null;
        WemInfo info;
        try
        {
            info = WemInfo.Read(riff);
        }
        catch (InvalidDataException ex)
        {
            Wave.Show(null, 48000);
            WaveMessage.Text = L.T("SoundWindow.IsntSoundCanRead", name, ex.Message);
            WaveMessage.Visibility = Visibility.Visible;
            PlayButton.IsEnabled = WavButton.IsEnabled = false;
            return;
        }
        // a bank's or an event's own summary stays; a sound alone is summed up by what it says about itself
        if (_bank is null && !_path.StartsWith(SoundLinks.Event, StringComparison.Ordinal)) DetailText.Text = Summary(info);
        DetailList.ItemsSource = Details(info, where);
        WaveMessage.Text = SoundDecoder.CanPlay(info.Codec) ? L.T("SoundWindow.ReadingSound") : L.T("SoundWindow.SoundsCantPlayedYet", info.CodecName);
        WaveMessage.Visibility = Visibility.Visible;
        Wave.Show(null, info.SampleRate);
        PlayButton.IsEnabled = WavButton.IsEnabled = false;
        if (!SoundDecoder.CanPlay(info.Codec)) return;
        try
        {
            var decoded = await Task.Run(() => SoundDecoder.Decode(riff, info));
            if (version != _soundVersion) return;
            _decoded = decoded;
            _player = new SoundPlayer(decoded.Samples, decoded.Channels, decoded.Rate) { Loop = LoopCheck.IsChecked == true, Volume = (float)VolumeSlider.Value };
            _player.Ended += (_, _) => Dispatcher.BeginInvoke(() => { PlayIcon.Text = ""; _timer.Stop(); ShowPosition(); });
            double rate = Math.Max(1, info.SampleRate);
            Wave.Show(_player.Stereo, _player.Rate,
                info.Loop is var (start, end) ? (start / rate, end / rate) : null,
                info.Markers.Select(m => (m.Sample / rate, m.Label ?? "")).ToList());
            WaveMessage.Visibility = Visibility.Collapsed;
            PlayButton.IsEnabled = WavButton.IsEnabled = true;
            ShowPosition();
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        {
            if (version == _soundVersion) WaveMessage.Text = L.T("SoundWindow.CouldNotPlayed", name, ex.Message);
        }
    }

    private static string Summary(WemInfo info) =>
        $"{info.CodecName}  ·  {info.Speakers}  ·  {info.SampleRate:N0} Hz" + (info.Duration > 0 ? $"  ·  {info.Duration:0.00} s" : "") + $"  ·  {Format.Bytes(info.Size)}"
        + (info.Loop is not null ? L.T("SoundWindow.Loops") : "");

    private List<DetailRow> Details(WemInfo info, string? where)
    {
        var rows = new List<DetailRow>
        {
            new(L.T("SoundWindow.Codec"), $"{info.CodecName} (0x{info.Codec:X4})" + (SoundDecoder.CanPlay(info.Codec) ? "" : L.T("SoundWindow.NotPlayedYet"))),
            new(L.T("SoundWindow.Channels"), $"{info.Channels} ({info.Speakers})" + (info.ChannelMask != 0 ? L.T("SoundWindow.MaskX", info.ChannelMask) : "")),
            new(L.T("SoundWindow.SampleRate"), $"{info.SampleRate:N0} Hz"),
        };
        if (info.Duration > 0) rows.Add(new(L.T("Common.Length"), L.T("SoundWindow.SamplesChannel", info.Duration, info.Samples)));
        double bitrate = info.Duration > 0 ? info.Size * 8 / info.Duration / 1000 : info.AverageBytesPerSecond * 8 / 1000.0;
        if (bitrate > 0) rows.Add(new(L.T("SoundWindow.Bitrate"), L.T("SoundWindow.Kbit", bitrate)));
        if (info.BlockAlign > 0) rows.Add(new(L.T("SoundWindow.BlockSize"), L.T("SoundWindow.Bytes", info.BlockAlign, (info.BitsPerSample > 0 ? L.T("SoundWindow.BitsSample", info.BitsPerSample) : ""))));
        double rate = Math.Max(1, info.SampleRate);
        rows.Add(new(L.T("SoundWindow.Loop"), info.Loop is var (start, end) ? L.T("SoundWindow.Samples", start / rate, end / rate, start, end) : L.T("Common.None")));
        rows.Add(new(L.T("SoundWindow.MarkedPoints"), info.Markers.Count == 0 ? "none"
            : string.Join("\n", info.Markers.Select(m => $"{m.Sample / rate:0.000} s" + (m.Label is { Length: > 0 } label ? $"  {label}" : L.T("SoundWindow.Cue", m.Id))))));
        rows.Add(new(L.T("Common.Size"), L.T("SoundWindow.Bytes2", Format.Bytes(info.Size), info.Size)));
        rows.Add(new(L.T("SoundWindow.Parts"), string.Join("\n", info.Chunks.Select(c => L.T("SoundWindow.Bytes3", c.Id.TrimEnd(), c.Size)))));
        rows.Add(new(L.T("SoundWindow.Kept2"), where ?? $"{_path}\n{_source}"));
        return rows;
    }

    // ---------------------------------------------------------------- a voice line

    /// <summary>A voice line's words in a language, who says it, and its lip sync.</summary>
    private async Task ShowLineAsync(W3Language language, uint id)
    {
        SubtitleCard.Visibility = Visibility.Visible;
        _syncing = true;
        LanguageBox.ItemsSource = W3Strings.Languages;
        LanguageBox.SelectedItem = language;
        _syncing = false;
        await ShowWordsAsync(language, id);
        string? game = GameLocator.Resolve(null);
        if (game is null) return;
        var speech = await W3Speech.ForLanguage(game, language);
        var file = speech.FirstOrDefault(s => s.Has(id));
        string lip = file?.LipSync(id) is { } blob
            ? (TryLipSync(blob) is { } length ? L.T("SoundWindow.LipSync", length) : L.T("SoundWindow.LipSync2")) : L.T("SoundWindow.NoLipSync");
        LineText.Text = L.T("SoundWindow.StringLip", id, lip) + (file is not null ? $"  ·  {Path.GetFileName(file.Path)}" : "") + (LineText.Text.Length > 0 ? "  ·  " + LineText.Text : "");
    }

    private static float? TryLipSync(byte[] blob)
    {
        try { return Core.Animation.LipSync.Read(blob).Duration; }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return null; }
    }

    private async Task ShowWordsAsync(W3Language language, uint id)
    {
        string? game = GameLocator.Resolve(null);
        if (game is null)
        {
            SubtitleText.Text = L.T("SoundWindow.GameFolderIsntSet");
            return;
        }
        SubtitleText.Text = L.T("SoundWindow.ReadingStrings", language.Handle);
        try
        {
            var index = await GameStringIndex.GetAsync(game, language);
            if (LanguageBox.SelectedItem is W3Language picked && picked != language) return;
            var found = index.WithId(id);
            if (found.Count == 0)
            {
                SubtitleText.Text = L.T("SoundWindow.NoStringHasID", language.Handle, id);
                SpeakerText.Text = "";
                return;
            }
            var said = found[^1];
            SubtitleText.Text = said.Text;
            SpeakerText.Text = said.Voiceover is { Length: > 0 } voice ? L.T("SoundWindow.Said", voice) : "";
            if (said.KeyName is { Length: > 0 } key && !LineText.Text.Contains(key, StringComparison.Ordinal)) LineText.Text = L.T("SoundWindow.Key", key);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SubtitleText.Text = L.T("SoundWindow.GamesStringsCouldNot");
        }
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _line is not { } line || LanguageBox.SelectedItem is not W3Language language) return;
        _ = ShowWordsAsync(language, line.StringId);
    }

    // ---------------------------------------------------------------- banks

    /// <summary>The banks that play a streamed sound, from every bank of the game's sound caches.</summary>
    private async Task ShowBanksAsync(uint id)
    {
        string? game = GameLocator.Resolve(null);
        if (game is null)
        {
            BanksNote.Text = L.T("SoundWindow.GameFolderIsntSet2");
            return;
        }
        BanksNote.Text = L.T("SoundWindow.LookingThroughEverySound");
        var index = await SoundBankIndex.ForGame(game);
        var playing = index.PlayedBy.GetValueOrDefault(id) ?? [];
        BanksTab.Header = L.T("SoundWindow.Played2", playing.Count);
        BankGrid.ItemsSource = playing.Select(p => new BankRow(p.Bank, p.Sound.ObjectId.ToString(), p.Sound.Kind)).ToList();
        BanksNote.Text = playing.Count == 0 ? L.T("SoundWindow.NoBankPlays") : L.T("SoundWindow.BanksPlayEachThrough");
    }

    private void BankGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BankGrid.SelectedItem is BankRow row) _ = DataViewers.OpenLinkAsync(SoundCacheArchive.Banks + row.Bank, _locator, this, this);
    }

    /// <summary>A bank: its sounds, kept in it or streamed, each to play, and its objects by kind.</summary>
    private async Task ShowBankAsync(byte[] data)
    {
        try
        {
            _bank = await Task.Run(() => SoundBank.Read(data));
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            BusyText.Text = L.T("SoundWindow.BankCouldNotRead", ex.Message);
            BusyText.Visibility = Visibility.Visible;
            Body.Visibility = Visibility.Hidden;
            return;
        }
        var bank = _bank;
        DetailsTab.Header = L.T("SoundWindow.SoundDetails");
        BanksTab.Visibility = Visibility.Collapsed;
        TitleText.Text = Path.GetFileName(_path);
        int events = bank.Objects.GetValueOrDefault("Events");
        DetailText.Text = L.T("SoundWindow.WwiseSoundBankVersion", bank.Version, bank.Id, L.T("Count.Sound", bank.Embedded.Count), L.T("Count.Sound", bank.Sounds.Where(s => s.Streamed).Select(s => s.SourceId).Distinct().Count()), L.T("Count.Event", events));
        if (bank.Version != Core.Packing.SoundCacheWriter.GameBankVersion)
            DetailText.Text += "  ·  " + L.T("SoundWindow.GameWontLoadBank", Core.Packing.SoundCacheWriter.GameBankVersion);
        var objects = bank.Objects.OrderByDescending(o => o.Value).Select(o => new DetailRow(o.Key, $"{o.Value:N0}")).ToList();
        objects.Add(new DetailRow("", ""));
        objects.AddRange(bank.Sections.Select(s => new DetailRow(L.T("SoundWindow.Section", s.Id), L.T("SoundWindow.Bytes4", s.Size))));
        ObjectList.ItemsSource = objects;
        ObjectsTab.Header = L.T("SoundWindow.Objects2", bank.Objects.Values.Sum());
        SoundsNote.Text = L.T("SoundWindow.ReadingBanksSounds");
        Tabs.SelectedItem = SoundsTab;

        var rows = await RowsAsync(bank.Sounds, bank);
        SoundGrid.ItemsSource = rows;
        SoundsTab.Header = L.T("SoundWindow.Sounds2", rows.Count);
        int playable = rows.Count(r => r.Info is { } i && SoundDecoder.CanPlay(i.Codec));
        SoundsNote.Text = L.T("SoundWindow.BankPlaysKeepsCan", L.T("Count.Sound", rows.Count), playable);
        PickFirst(rows);
    }

    private void PickFirst(List<SoundRow> rows)
    {
        if (rows.FirstOrDefault(r => r.Info is { } i && SoundDecoder.CanPlay(i.Codec)) is { } first) SoundGrid.SelectedItem = first;
        else if (rows.Count > 0) SoundGrid.SelectedIndex = 0;
    }

    /// <summary>
    /// The rows of sounds, each where the game keeps it: streamed (whole, even when a bank keeps its start), in the bank
    /// shown (<paramref name="own"/>), or in another bank (a media bank), with what the sound says about itself.
    /// </summary>
    private async Task<List<SoundRow>> RowsAsync(IReadOnlyList<BankSound> sounds, SoundBank? own)
    {
        string? game = GameLocator.Resolve(null);
        var caches = game is null ? [] : await SoundCache.ForGame(game);
        // a sound neither kept in the bank nor streamed is kept in another bank: the index says which
        SoundBankIndex? index = null;
        if (game is not null && sounds.Any(s => own?.Embedded.ContainsKey(s.SourceId) != true && !caches.Any(c => c.Has($"{s.SourceId}.wem"))))
        {
            SoundsNote.Text = L.T("SoundWindow.FindingBanksKeepSounds");
            index = await SoundBankIndex.ForGame(game);
        }
        return await Task.Run(() =>
        {
            var list = new List<SoundRow>();
            var seen = new HashSet<uint>();
            var others = new Dictionary<string, SoundBank?>(StringComparer.OrdinalIgnoreCase);
            foreach (var sound in sounds)
            {
                if (!seen.Add(sound.SourceId)) continue;
                // a streamed sound first: a bank may keep the start of one too, to begin it at once, but only the start
                string streamed = $"{sound.SourceId}.wem";
                if (caches.FirstOrDefault(c => c.Has(streamed)) is { } cache)
                {
                    list.Add(new SoundRow(sound.SourceId, sound, false, null, sound.Kind, Info(cache.ReadHead(streamed))));
                    continue;
                }
                if (own is not null && own.Embedded.ContainsKey(sound.SourceId))
                {
                    list.Add(new SoundRow(sound.SourceId, sound, true, null, sound.Kind, Info(own.EmbeddedSound(sound.SourceId))));
                    continue;
                }
                string? other = index?.KeptIn.GetValueOrDefault(sound.SourceId);
                if (other is not null && !others.ContainsKey(other))
                    others[other] = caches.FirstOrDefault(c => c.Has(other))?.Read(other) is { } data ? SoundBank.Read(data) : null;
                list.Add(new SoundRow(sound.SourceId, sound, false, other, sound.Kind, Info(other is null ? null : others[other]?.EmbeddedSound(sound.SourceId))));
            }
            // those the bank keeps that no object of its own plays (another bank's)
            if (own is not null)
                foreach (var id in own.Embedded.Keys.Where(seen.Add))
                    list.Add(new SoundRow(id, null, true, null, L.T("SoundWindow.AnotherBanksObject"), Info(own.EmbeddedSound(id))));
            return list;
        });
    }

    /// <summary>The name Wwise gives an action's type.</summary>
    private static string ActionName(ushort type) => (type >> 8) switch
    {
        0x04 => type == 0x0403 ? L.T("SoundWindow.Plays") : L.T("SoundWindow.PlaysContinues"),
        0x01 => L.T("SoundWindow.Stops"),
        0x02 => L.T("SoundWindow.Pauses"),
        0x03 => L.T("SoundWindow.Resumes"),
        0x12 => L.T("SoundWindow.SetsState"),
        0x19 => L.T("SoundWindow.SetsSwitch"),
        0x13 or 0x14 => L.T("SoundWindow.SetsGameParameter"),
        0x06 or 0x07 => L.T("SoundWindow.MutesUnmutes"),
        0x0A or 0x0B => L.T("SoundWindow.SetsVolume"),
        0x08 or 0x09 => L.T("SoundWindow.SetsPitch"),
        0x0E or 0x0F => L.T("SoundWindow.SetsLowPassFilter"),
        0x1E => L.T("SoundWindow.Seeks"),
        0x1C => L.T("SoundWindow.PostsEvent"),
        _ => L.T("SoundWindow.ActionX", type),
    };

    /// <summary>A sound event: the sounds its play actions can play (a random container's choices, a switch's cases), and its actions.</summary>
    private async Task ShowEventAsync(string name)
    {
        string? game = GameLocator.Resolve(null);
        if (game is null)
        {
            BusyText.Text = L.T("SoundWindow.GameFolderIsntSet3");
            return;
        }
        BusyText.Text = L.T("SoundWindow.FindingSoundEventGames", name);
        var index = await SoundBankIndex.ForGame(game);
        uint id = SoundBankIndex.Hash(name);
        if (!index.Events.TryGetValue(id, out var e))
        {
            BusyText.Text = L.T("SoundWindow.NoSoundBankHas", name, id);
            return;
        }
        BusyText.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        _soundId = id;
        BanksTab.Visibility = Visibility.Collapsed;
        DetailsTab.Header = L.T("SoundWindow.SoundDetails");
        Title = L.T("SoundWindow.SoundEvent2", name);
        TitleText.Text = name;
        var sounds = index.SoundsOf(name);
        string does = string.Join(", ", e.Actions.Select(a => ActionName(a.Action)).Distinct());
        DetailText.Text = L.T("SoundWindow.EventDetail", id, Path.GetFileNameWithoutExtension(e.Bank), does.Length > 0 ? does : L.T("SoundWindow.DoesNothing"))
                        + (sounds.Count == 0 ? L.T("SoundWindow.PlaysNoSound") : L.T("SoundWindow.CanPlay", L.T("Count.Sound", sounds.Count)));
        InfoText.Text = L.T("SoundWindow.In", name, SoundCacheArchive.Banks, e.Bank);
        ObjectList.ItemsSource = e.Actions.Select(a => new DetailRow(ActionName(a.Action), L.T("SoundWindow.Object2", a.Target))).Prepend(new DetailRow(L.T("SoundWindow.Bank3"), e.Bank)).ToList();
        ObjectsTab.Header = L.T("SoundWindow.Actions", e.Actions.Count);
        Tabs.SelectedItem = SoundsTab;
        if (sounds.Count == 0)
        {
            SoundsNote.Text = e.Actions.Any(a => a.Action >> 8 == 0x04) ? L.T("SoundWindow.PlayActionsPlayNo") : L.T("SoundWindow.PlaysNothing", does.ToLowerInvariant());
            WaveMessage.Text = L.T("SoundWindow.EventPlaysNoSound");
            WaveMessage.Visibility = Visibility.Visible;
            PlayButton.IsEnabled = WavButton.IsEnabled = false;
            return;
        }
        SoundsNote.Text = L.T("SoundWindow.ReadingSounds");
        var rows = await RowsAsync(sounds.Select(s => s.Sound).ToList(), null);
        SoundGrid.ItemsSource = rows;
        SoundsTab.Header = L.T("SoundWindow.Sounds2", rows.Count);
        SoundsNote.Text = rows.Count == 1 ? L.T("SoundWindow.SoundPlaysClickPlay")
            : L.T("SoundWindow.SoundsCanPlayOne", rows.Count);
        PickFirst(rows);
    }

    private static WemInfo? Info(byte[]? riff)
    {
        if (riff is null) return null;
        try { return WemInfo.Read(riff); }
        catch (InvalidDataException) { return null; }
    }

    private async void SoundGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SoundGrid.SelectedItem is not SoundRow row) return;
        byte[]? riff = row.InBank ? _bank?.EmbeddedSound(row.Id) : null;
        string where = row.InBank ? L.T("SoundWindow.In3", Path.GetFileName(_path)) : row.OtherBank is { } other ? L.T("SoundWindow.In4", SoundCacheArchive.Banks, other) : L.T("SoundWindow.StreamedWem", SoundCacheArchive.Streamed, row.Id);
        if (riff is null && GameLocator.Resolve(null) is { } game)
        {
            var caches = await SoundCache.ForGame(game);
            var index = row.OtherBank is null ? null : await SoundBankIndex.ForGame(game);
            riff = await Task.Run(() => index is not null ? index.Sound(caches, row.Id) : caches.FirstOrDefault(c => c.Has($"{row.Id}.wem"))?.Read($"{row.Id}.wem"));
        }
        if (riff is null)
        {
            WaveMessage.Text = L.T("SoundWindow.SoundIsntBankGames", row.Id);
            WaveMessage.Visibility = Visibility.Visible;
            return;
        }
        await ShowSoundAsync(riff, $"{row.Id}.wem", row.Id, where);
    }

    private void CopySoundId_Click(object sender, RoutedEventArgs e)
    {
        if (SoundGrid.SelectedItem is SoundRow row) Copy(row.Id.ToString());
    }

    private void OpenSound_Click(object sender, RoutedEventArgs e)
    {
        if (SoundGrid.SelectedItem is not SoundRow row) return;
        if (row.InBank && _bank?.EmbeddedSound(row.Id) is { } riff)
            new SoundWindow($"{row.Id}.wem", L.T("SoundWindow.In5", Path.GetFileName(_path)), () => riff, _locator).Standalone(this).Show();
        else if (row.OtherBank is { } other)
            _ = OpenFromOtherBankAsync(row.Id, other);
        else
            _ = DataViewers.OpenLinkAsync($"{SoundCacheArchive.Streamed}{row.Id}.wem", _locator, this, this);
    }

    /// <summary>Opens a sound another bank keeps in a window of its own.</summary>
    private async Task OpenFromOtherBankAsync(uint id, string bankName)
    {
        if (GameLocator.Resolve(null) is not { } game) return;
        var caches = await SoundCache.ForGame(game);
        var index = await SoundBankIndex.ForGame(game);
        if (await Task.Run(() => index.Sound(caches, id)) is { } riff)
            new SoundWindow($"{id}.wem", L.T("SoundWindow.In6", bankName), () => riff, _locator).Standalone(this).Show();
    }

    // ---------------------------------------------------------------- playing

    private void ShowPosition()
    {
        if (_player is not { } player)
        {
            TimeText.Text = "";
            return;
        }
        Wave.Position = player.Position;
        TimeText.Text = $"{Time(player.Position)} / {Time(player.Duration)}";
        if (!player.IsPlaying && _timer.IsEnabled && player.Position >= player.Duration - 0.001)
        {
            _timer.Stop();
            PlayIcon.Text = "";
        }
    }

    private static string Time(double seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss\.ff" : @"m\:ss\.ff");

    private void TogglePlay()
    {
        if (_player is not { } player) return;
        if (player.IsPlaying)
        {
            player.Pause();
            _timer.Stop();
            PlayIcon.Text = "";
        }
        else
        {
            player.Play();
            _timer.Start();
            PlayIcon.Text = "";
        }
        ShowPosition();
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _player?.Stop();
        _timer.Stop();
        PlayIcon.Text = "";
        ShowPosition();
    }

    private void Wave_Seek(object? sender, double time)
    {
        if (_player is null) return;
        _player.Position = time;
        ShowPosition();
    }

    private void LoopCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_player is not null) _player.Loop = LoopCheck.IsChecked == true;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is not null) _player.Volume = (float)VolumeSlider.Value;
    }

    // ---------------------------------------------------------------- saving

    private string BaseName => Path.GetFileNameWithoutExtension(_soundName.Length > 0 ? _soundName : _path);

    private void WavButton_Click(object sender, RoutedEventArgs e)
    {
        if (_decoded is not var (samples, channels, rate)) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = BaseName + ".wav", Filter = L.T("SoundWindow.WAVSoundWavWav"), Title = L.T("SoundWindow.SaveSoundWAVFile2") };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllBytes(dialog.FileName, WwiseOpus.ToWav(samples, channels, rate)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ShowMessage(L.T("SoundWindow.CouldNotSaved"), ex.Message, MessageKind.Error); }
    }

    /// <summary>RedSpeech Tool (by nikich340), which makes voice lines: packs .w3speech files, converts WAV to WEM and lip sync.</summary>
    public const string RedSpeechUrl = "https://www.nexusmods.com/witcher3/mods/14750"; // loc-ignore

    private void RedSpeech_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RedSpeechUrl) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private void WemButton_Click(object sender, RoutedEventArgs e)
    {
        if (_riff is not { } riff) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = BaseName + ".wem", Filter = L.T("SoundWindow.WwiseSoundWemWem"), Title = L.T("SoundWindow.SaveSoundGameKeeps2") };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllBytes(dialog.FileName, riff); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = ShowMessage(L.T("SoundWindow.CouldNotSaved"), ex.Message, MessageKind.Error); }
    }

    private static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_line is { } line) Copy(line.StringId.ToString());
        else if (_soundId is { } id) Copy(id.ToString());
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Message.IsOpen || e.OriginalSource is TextBox) return;
        switch (e.Key)
        {
            case Key.Space when Keyboard.Modifiers == ModifierKeys.None:
                TogglePlay();
                break;
            case Key.Home:
                StopButton_Click(this, new RoutedEventArgs());
                break;
            case Key.L when Keyboard.Modifiers == ModifierKeys.None:
                LoopCheck.IsChecked = LoopCheck.IsChecked != true;
                LoopCheck_Click(this, new RoutedEventArgs());
                break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control && e.OriginalSource is not DataGridCell:
                CopyButton_Click(this, new RoutedEventArgs());
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
