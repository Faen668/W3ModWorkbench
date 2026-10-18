using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using W3ModWorkbench.Core.Meshes;

namespace W3ModWorkbench.ViewModels;

/// <summary>
/// One of a material's parameters in the mesh viewer, as text to change: a texture's path, a number, a colour
/// (<c>#RRGGBB</c>, or <c>#RRGGBBAA</c> with its alpha) or a vector (<c>x, y, z, w</c>). Changing one that the material
/// takes from its base makes it the material's own.
/// </summary>
public sealed class MaterialParameterItem : INotifyPropertyChanged
{
    private readonly Action<MaterialParameterItem, object> _changed;
    private string _text;
    private string _error = "";

    public MaterialParameterItem(MaterialParameter parameter, bool own, bool overridesBase, Action<MaterialParameterItem, object> changed)
    {
        Parameter = parameter;
        IsOwn = own;
        OverridesBase = overridesBase;
        _changed = changed;
        _text = Format(parameter.Value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MaterialParameter Parameter { get; }
    public string Name => Parameter.Name;

    /// <summary>Set by the material itself, not taken from a base.</summary>
    public bool IsOwn { get; }

    /// <summary>Set by the material and by a base, so taking the material's own away goes back to the base's.</summary>
    public bool OverridesBase { get; }

    public bool IsEditable => Parameter.Kind != ParameterKind.Other;
    public bool IsReadOnly => !IsEditable;
    public bool IsTexture => Parameter.Kind == ParameterKind.Texture;
    public FontWeight Weight => IsOwn ? FontWeights.SemiBold : FontWeights.Normal;
    public Visibility PickVisibility => IsTexture ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResetVisibility => IsOwn && OverridesBase ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SwatchVisibility => Parameter.Kind == ParameterKind.Color ? Visibility.Visible : Visibility.Collapsed;

    public string Detail => (IsOwn ? "its own" : "from " + Parameter.From) + "  ·  " + Parameter.Type
        + (Parameter.Kind == ParameterKind.Other ? "  ·  not changed here" : "");

    public Brush? Swatch => Parameter.Value is ColorValue c ? Frozen(new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B))) : null;

    /// <summary>The value as text. A change that reads as a value of the parameter's kind is made; anything else says what's wrong.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            if (TryParse(value, out var parsed, out string error))
            {
                Error = "";
                if (!Equals(Format(parsed), Format(Parameter.Value))) _changed(this, parsed);
            }
            else Error = error;
        }
    }

    public string Error
    {
        get => _error;
        private set
        {
            _error = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Error)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorVisibility)));
        }
    }

    public Visibility ErrorVisibility => _error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    public static string Format(object? value) => value switch
    {
        TextureRef t => t.Path ?? "",
        float f => f.ToString("0.#####", CultureInfo.InvariantCulture),
        ColorValue c => c.ToString(),
        Vector4 v => string.Join(", ", new[] { v.X, v.Y, v.Z, v.W }.Select(x => x.ToString("0.#####", CultureInfo.InvariantCulture))),
        RawValue r => $"({r.Bytes.Length} bytes)",
        _ => "",
    };

    private bool TryParse(string text, out object value, out string error)
    {
        text = text.Trim();
        value = Parameter.Value!;
        error = "";
        switch (Parameter.Kind)
        {
            case ParameterKind.Texture:
                value = new TextureRef(text.Length == 0 ? null : text.Replace('/', '\\'));
                return true;
            case ParameterKind.Scalar:
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) { value = f; return true; }
                error = "A number, such as 0.5.";
                return false;
            case ParameterKind.Color:
                if (ParseColor(text) is { } c) { value = c; return true; }
                error = "A colour: #RRGGBB, #RRGGBBAA, or four numbers from 0 to 255.";
                return false;
            case ParameterKind.Vector:
                var parts = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
                var numbers = new float[4];
                if (parts.Length is >= 1 and <= 4 && parts.Select((p, i) => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])).All(ok => ok))
                {
                    value = new Vector4(numbers[0], numbers[1], numbers[2], numbers[3]);
                    return true;
                }
                error = "Up to four numbers: x, y, z, w.";
                return false;
            default:
                error = "This kind of parameter isn't changed here.";
                return false;
        }
    }

    private static ColorValue? ParseColor(string text)
    {
        if (text.StartsWith('#') && (text.Length == 7 || text.Length == 9)
            && uint.TryParse(text[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hex))
            return text.Length == 7
                ? new ColorValue((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex, 255)
                : new ColorValue((byte)(hex >> 24), (byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
        var parts = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 3 or 4 && parts.All(p => byte.TryParse(p, out _)))
            return new ColorValue(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]), parts.Length == 4 ? byte.Parse(parts[3]) : (byte)255);
        return null;
    }
}
