using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using W3ModWorkbench.Core.Meshes;
using W3ModWorkbench.Localisation;

namespace W3ModWorkbench.ViewModels;

/// <summary>
/// One of a material's parameters in the material viewer, to look at and change: its value as text, a texture's
/// thumbnail, a number's slider, a colour's swatch and channels, and whether the material sets it itself or takes it
/// from a base. A change goes to <c>changed</c>, which makes it the material's own.
/// </summary>
public sealed class MaterialValueRow : INotifyPropertyChanged
{
    private readonly Action<MaterialValueRow, object> _changed;
    private string _text = "", _error = "";
    private BitmapSource? _thumbnail;
    private bool _colourOpen, _setting;

    public MaterialValueRow(MaterialParameter parameter, bool own, bool inBase, Action<MaterialValueRow, object> changed)
    {
        Parameter = parameter;
        IsOwn = own;
        InBase = inBase;
        _changed = changed;
        _text = MaterialParameterItem.Format(parameter.Value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MaterialParameter Parameter { get; private set; }

    /// <summary>Whether the material sets it itself.</summary>
    public bool IsOwn { get; private set; }

    /// <summary>Whether a base or the shader has it too, so taking the material's own away leaves the base's.</summary>
    public bool InBase { get; }

    public string Name => Parameter.Name;
    public ParameterKind Kind => Parameter.Kind;

    /// <summary>The group it's listed under.</summary>
    public string Group => Kind switch
    {
        ParameterKind.Texture => L.T("Common.Textures"),
        ParameterKind.Color => L.T("MaterialValueRow.Colours"),
        ParameterKind.Scalar => L.T("MaterialValueRow.Numbers"),
        ParameterKind.Vector => L.T("MaterialValueRow.Vectors"),
        _ => L.T("MaterialValueRow.Other"),
    };

    /// <summary>Where its group comes in the list: textures, colours, numbers, vectors, then the rest.</summary>
    public int GroupOrder => Kind switch { ParameterKind.Texture => 0, ParameterKind.Color => 1, ParameterKind.Scalar => 2, ParameterKind.Vector => 3, _ => 4 };

    public string Where => IsOwn ? (InBase ? L.T("MaterialValueRow.OwnOverBases") : L.T("MaterialValueRow.Own")) : L.T("MaterialValueRow.From", Parameter.From);
    public double WhereOpacity => IsOwn ? 1 : 0.6;
    public FontWeight Weight => IsOwn ? FontWeights.SemiBold : FontWeights.Normal;
    public string Tip => $"{Name}  ·  {Parameter.Type}  ·  {Where}";

    public bool IsTexture => Kind == ParameterKind.Texture;
    public bool IsColour => Kind == ParameterKind.Color;
    public bool IsNumber => Kind == ParameterKind.Scalar;
    public bool IsEditable => Kind != ParameterKind.Other;
    public Visibility TextureVisibility => IsTexture ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ColourVisibility => IsColour ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NumberVisibility => IsNumber ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResetVisibility => IsOwn && InBase ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemoveVisibility => IsOwn && !InBase ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A texture's path, to open it; null for anything else or none.</summary>
    public string? TexturePath => Parameter.Value is TextureRef { Path: { Length: > 0 } p } ? p : null;
    public Visibility LinkVisibility => TexturePath is null ? Visibility.Collapsed : Visibility.Visible;

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; Changed(nameof(Thumbnail)); }
    }

    /// <summary>The value as text, to type a new one: a path, a number, #RRGGBB(AA), or x, y, z, w.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            if (Parse(value) is { } parsed)
            {
                Error = "";
                if (MaterialParameterItem.Format(parsed) != MaterialParameterItem.Format(Parameter.Value)) _changed(this, parsed);
            }
            else Error = Kind switch
            {
                ParameterKind.Scalar => L.T("MaterialValueRow.NumberSuch"),
                ParameterKind.Color => L.T("MaterialValueRow.ColourRrggbbRrggbbaaThree"),
                ParameterKind.Vector => L.T("MaterialValueRow.UpFourNumbersX"),
                _ => L.T("MaterialValueRow.KindParameterIsntChanged"),
            };
        }
    }

    public string Error
    {
        get => _error;
        private set { _error = value; Changed(nameof(Error)); Changed(nameof(ErrorVisibility)); }
    }

    public Visibility ErrorVisibility => _error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    // ---------------------------------------------------------------- a number

    public double Number
    {
        get => Parameter.Value is float f ? f : 0;
        set { if (!_setting && Kind == ParameterKind.Scalar) _changed(this, (float)Math.Round(value, 4)); }
    }

    /// <summary>How far its slider goes: 1 for a fraction, else past the value, to a round number.</summary>
    public double NumberMaximum { get; private set; } = 1;
    public double NumberMinimum => Math.Min(0, Number);

    private void SetRange()
    {
        double v = Math.Abs(Number);
        NumberMaximum = v <= 1 ? 1 : Math.Pow(10, Math.Ceiling(Math.Log10(v * 2)));
    }

    // ---------------------------------------------------------------- a colour

    private ColorValue Colour => Parameter.Value as ColorValue ?? new ColorValue(0, 0, 0, 255);
    public Brush? Swatch => IsColour ? Frozen(new SolidColorBrush(Color.FromRgb(Colour.R, Colour.G, Colour.B))) : null;

    /// <summary>Whether its channels are shown to change.</summary>
    public bool ColourOpen
    {
        get => _colourOpen;
        set { _colourOpen = value; Changed(nameof(ColourOpen)); Changed(nameof(ChannelsVisibility)); }
    }

    public Visibility ChannelsVisibility => _colourOpen && IsColour ? Visibility.Visible : Visibility.Collapsed;

    public double Red { get => Colour.R; set => SetChannel(0, value); }
    public double Green { get => Colour.G; set => SetChannel(1, value); }
    public double Blue { get => Colour.B; set => SetChannel(2, value); }
    public double Alpha { get => Colour.A; set => SetChannel(3, value); }

    private void SetChannel(int channel, double value)
    {
        if (_setting || !IsColour) return;
        var c = Colour;
        byte v = (byte)Math.Clamp(Math.Round(value), 0, 255);
        _changed(this, channel switch
        {
            0 => c with { R = v },
            1 => c with { G = v },
            2 => c with { B = v },
            _ => c with { A = v },
        });
    }

    // ---------------------------------------------------------------- changes

    /// <summary>Takes a new value as the material's own (or the base's, when it's given back), showing it everywhere.</summary>
    public void Show(MaterialParameter parameter, bool own)
    {
        _setting = true;
        Parameter = parameter;
        IsOwn = own;
        _text = MaterialParameterItem.Format(parameter.Value);
        if (Kind == ParameterKind.Scalar && Math.Abs(Number) > NumberMaximum) SetRange();
        Error = "";
        foreach (string name in new[] { nameof(Parameter), nameof(Text), nameof(Number), nameof(NumberMaximum), nameof(NumberMinimum), nameof(Swatch),
                     nameof(Red), nameof(Green), nameof(Blue), nameof(Alpha), nameof(IsOwn), nameof(Where), nameof(WhereOpacity), nameof(Weight),
                     nameof(Tip), nameof(ResetVisibility), nameof(RemoveVisibility), nameof(TexturePath), nameof(LinkVisibility) })
            Changed(name);
        _setting = false;
    }

    private object? Parse(string text)
    {
        text = text.Trim();
        switch (Kind)
        {
            case ParameterKind.Texture:
                return new TextureRef(text.Length == 0 ? null : text.Replace('/', '\\'));
            case ParameterKind.Scalar:
                return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : null;
            case ParameterKind.Color:
                if (text.StartsWith('#') && text.Length is 7 or 9 && uint.TryParse(text[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
                    return text.Length == 7 ? new ColorValue((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex, 255)
                        : new ColorValue((byte)(hex >> 24), (byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
                var channels = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
                if (channels.Length is 3 or 4 && channels.All(p => byte.TryParse(p, out _)))
                    return new ColorValue(byte.Parse(channels[0]), byte.Parse(channels[1]), byte.Parse(channels[2]), channels.Length == 4 ? byte.Parse(channels[3]) : (byte)255);
                return null;
            case ParameterKind.Vector:
                var parts = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
                var numbers = new float[4];
                return parts.Length is >= 1 and <= 4 && parts.Select((p, i) => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])).All(ok => ok)
                    ? new Vector4(numbers[0], numbers[1], numbers[2], numbers[3]) : null;
            default:
                return null;
        }
    }

    /// <summary>Its slider's range, set when it's made.</summary>
    public MaterialValueRow WithRange()
    {
        SetRange();
        return this;
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
