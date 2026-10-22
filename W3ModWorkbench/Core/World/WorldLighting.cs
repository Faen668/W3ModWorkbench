using System.IO;
using System.Numerics;
using System.Text;
using W3ModWorkbench.Core.Cr2w;
using W3ModWorkbench.Core.Curves;
using W3ModWorkbench.Core.Environments;

namespace W3ModWorkbench.Core.World;

/// <summary>A world's light and sky at one hour: where the light comes from and the colours of the sky, the sun, the fog and the water, linear.</summary>
public readonly record struct LightingAt(
    Vector3 LightDirection, Vector3 SunDirection, bool Moon,
    Vector3 LightColour, Vector3 Ambient,
    Vector3 SkyZenith, Vector3 SkyHorizon, Vector3 SunGlow, float SunGlowSize, float SkyBrightness,
    Vector3 FogFront, Vector3 FogMiddle, Vector3 FogBack, float FogDensity, float FogAppear, float FogAppearRange,
    float FogVerticalDensity, float FogVerticalOffset, float FogVerticalFront, float FogVerticalBack, float FogExponent,
    Vector3 AerialFront, Vector3 AerialMiddle, Vector3 AerialBack, float AerialExponent,
    Vector3 FogCustom, float FogCustomAmount, float FogCustomColourStart, float FogCustomColourRange,
    float FogCustomAmountStart, float FogCustomAmountRange, float FogDistanceLimit, float FogSkyDensity,
    Vector3 WaterColour, float WaterFog, float DayAmount, float LuminanceMin, float LuminanceMax);

/// <summary>
/// A weather condition from a world's weather table: its environment and how much of it blends in, its wind, how likely
/// it is to come next, how long it takes to blend in and how long it lasts.
/// </summary>
public sealed record WeatherCondition(string Name, string? Environment, float Blend, float Wind, float Probability, float BlendTime, float OccurrenceTime);

/// <summary>
/// The sky at one hour, as the game's sky shader takes it: the colours at the top, at the horizon, at the horizon
/// towards and away from the sun and the moon, and round each of them, how strongly each counts, the sun's and the
/// moon's directions, how fast the top colour takes over going up, and the sky's brightness; colours linear.
/// </summary>
public readonly record struct SkyAt(
    Vector3 Top, Vector3 Horizon, Vector3 SunHorizon, Vector3 SunBackHorizon, Vector3 SunSky,
    Vector3 MoonHorizon, Vector3 MoonBackHorizon, Vector3 MoonSky, Vector3 SunDirection, Vector3 MoonDirection,
    float SunSize, float MoonSize, float SunInfluence, float MoonInfluence, float Brightness, float VisibleScale, float HorizonAttenuation,
    float FogVerticalFront, float FogVerticalBack, float ReflectedSky);

/// <summary>
/// How the plants are shaded at one hour, as the engine's SpeedTree shaders take it (their compiled code, and REDkit's
/// shader source for the random colours): each kind's three random colours (rgb, linear) with their saturations in w and
/// the luminance weights the saturation works against; the plants' diffuse tint and how much of their ambient
/// occlusion counts; the billboards' tint; how translucent leaves are and the environment's translucency settings (view
/// dependency, base flatness, flat brightness, gain brightness); and the ambient light's scales in light and in shadow,
/// its colour, and the sky's and the ground's part of it (the remaster's IBL settings).
/// </summary>
public readonly record struct FoliageAt(
    Vector4[] Trees, Vector3 TreesLuminance, Vector4[] Branches, Vector3 BranchesLuminance, Vector4[] Grass, Vector3 GrassLuminance,
    Vector3 Diffuse, float OcclusionScale, Vector3 BillboardColour, float TranslucencyScale, Vector4 Translucency,
    float AmbientLight, float AmbientShadow, Vector3 AmbientColour,
    float SkyBrightness, float SkySaturation, float GroundBrightness, float GroundSaturation, float MaxSkyBrightness);

/// <summary>
/// The environment probes' settings at one hour: the sky they're made from (its colours at the top and at the horizon
/// and the shape between, as the engine's probe sky takes them), the light the scene adds to them, how strongly their
/// reflections count in light and in shadow, the ambient occlusion's settings (the HBAO+ ones: radius, bias, power, how
/// much it dims direct light), and the specular's: the foliage's f90 for grass and trees and the trees' specular scale.
/// </summary>
public readonly record struct ProbesAt(Vector3 SkyTop, Vector3 SkyHorizon, float SkyShape, Vector3 SceneAdd,
    float ReflectionLight, float ReflectionShadow, float AoRadius, float AoBias, float AoPower, float AoNonAmbient,
    float F90Grass, float F90Trees, float TreeSpecular, float WaterFresnel);

/// <summary>
/// An environment's tone mapping and colour grading at one hour, as the game's post-process shaders take them: the
/// exposure (from the adapted luminance, kept between the limits), the AgX curve's parameters, the balance maps' mix,
/// and the final colour balance's levels and its saturation and tint in the shadows, midtones and highlights.
/// </summary>
public readonly record struct GradingAt(
    float ExposureScale, float LuminanceShape, float LuminanceMin, float LuminanceMax,
    float GlobalContrast, float ShoulderPower, float ToePower, float BlackLift, Vector3 Slope, Vector3 Power, float Saturation, float MidGray,
    bool BalanceMaps, float BalanceMapAmount, float BalanceMapLerp, float PostBrightness,
    bool ParametricBalance, float LevelsShadows, float LevelsMidtones, float LevelsHighlights,
    Vector4 Low, Vector4 Mid, Vector4 High, float MidtoneRangeMin, float MidtoneRangeMax, float MidtoneMarginMin, float MidtoneMarginMax,
    float SkyLuminance, float SkyLuminanceAmount, Vector3 VignetteWeights, Vector3 VignetteColour, float VignetteOpacity);

/// <summary>
/// A world's lighting through the day, as its CGameWorld's environment parameters set it: the light's path (the global
/// lighting trajectory: a bearing, and curves of the sun's, the moon's and the light's height over the day, and which of
/// sun and moon lights the world), and its environment definition (.env) for the colours of the sky, the sun, the fog
/// and the water at each hour, with a weather condition's own environment from the world's weather table blended over
/// it as the game does. Colours in an .env are 0 to 255 with a strength in their fourth value.
/// </summary>
/// <summary>Which of an environment definition's parameter sets is used: the outdoor one, or REDkit's forced interior or cave one.</summary>
public enum EnvironmentParameters { Outdoors, Interiors, Caves }

/// <summary>One of the environments mixed into the lighting now, as REDkit's Env List shows it: its priority, its weight (0 to 1) and its file.</summary>
public readonly record struct ActiveEnvironment(int Priority, float Weight, string Path, bool Forced);

public sealed class WorldLighting
{
    private readonly float _bearing;
    private readonly RedCurve? _sunHeight, _moonHeight, _lightHeight, _lightChoice, _dayAmount;
    private readonly RedObject? _trajectory;
    private readonly Func<string, byte[]?>? _read;
    // every environment read, by file and parameter set, shared by the lightings made from one another
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, EnvironmentParameters), Settings> _cache;

    /// <summary>The environment definition the colours come from, or null when the world names none or it couldn't be read.</summary>
    public string? Environment { get; }

    /// <summary>The environment the world's scenes use (shown in the Env List; the game blends it in only in scenes).</summary>
    public string? ScenesEnvironment { get; }

    /// <summary>The world's weather conditions (its weather table's rows) and each one's environment, its blend, wind, and timings.</summary>
    public IReadOnlyList<WeatherCondition> Weathers { get; }

    /// <summary>The weather table's path, or null.</summary>
    public string? WeatherTable { get; }

    /// <summary>The weather condition blended in now (or being blended towards), or null for none.</summary>
    public string? Weather { get; }

    /// <summary>The weather condition being blended away from, or null.</summary>
    public string? PreviousWeather { get; }

    /// <summary>How far the change from <see cref="PreviousWeather"/> to <see cref="Weather"/> has come, 0 to 1.</summary>
    public float Transition { get; }

    /// <summary>
    /// The world's SpeedTree alpha scalars (its speedTreeParameters): how much a plant's picture's alpha is scaled before
    /// the 0.1 cut, for 3D plants, grass and billboards.
    /// </summary>
    public Vector3 AlphaScalars { get; private init; } = new(1, 1, 1);

    /// <summary>An environment forced over everything (REDkit's Force preset), or null.</summary>
    public string? Preset { get; }

    /// <summary>Which parameter set of each environment is used.</summary>
    public EnvironmentParameters Parameters { get; }

    /// <summary>How windy the weather is (its windScale, 0 to about 1; a light breeze with no weather).</summary>
    public float Wind
    {
        get
        {
            float Of(string? name) => Weathers.FirstOrDefault(w => w.Name == name) is { } w ? w.Wind : 0.2f;
            return Of(PreviousWeather) + (Of(Weather) - Of(PreviousWeather)) * (PreviousWeather is null ? 1 : Transition);
        }
    }

    /// <summary>An environment's settings that are set: curves, switches and the files it names, by their paths.</summary>
    private sealed class Settings
    {
        public Dictionary<string, RedCurve> Curves { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, bool> Switches { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
    }

    private WorldLighting(float bearing, RedObject? trajectory, string? environment, string? scenes, string? table,
        IReadOnlyList<WeatherCondition> weathers, string? weather, string? previous, float transition, string? preset,
        EnvironmentParameters parameters, Func<string, byte[]?>? read,
        System.Collections.Concurrent.ConcurrentDictionary<(string, EnvironmentParameters), Settings>? cache)
    {
        _bearing = bearing;
        RedCurve? Curve(string name) => trajectory?[name] is RedObject c ? RedCurve.FromSimpleCurve(c) : null;
        _sunHeight = Curve("sunHeight");
        _moonHeight = Curve("moonHeight");
        _lightHeight = Curve("lightHeight");
        _lightChoice = Curve("lightDirChoice");
        _dayAmount = Curve("skyDayAmount");
        _trajectory = trajectory;
        Environment = environment;
        ScenesEnvironment = scenes;
        WeatherTable = table;
        Weathers = weathers;
        Weather = weather;
        PreviousWeather = previous;
        Transition = Math.Clamp(transition, 0, 1);
        Preset = preset;
        Parameters = parameters;
        _read = read;
        _cache = cache ?? new();
    }

    private WorldLighting With(string? weather = null, string? previous = null, float? transition = null, string? preset = null,
        EnvironmentParameters? parameters = null, bool keepWeather = true, bool keepPreset = true) =>
        new(_bearing, _trajectory, Environment, ScenesEnvironment, WeatherTable, Weathers,
            keepWeather ? Weather : weather, keepWeather ? PreviousWeather : previous, transition ?? Transition,
            keepPreset ? Preset : preset, parameters ?? Parameters, _read, _cache) { AlphaScalars = AlphaScalars };

    /// <summary>
    /// A world's lighting: its environment parameters' trajectory, and its environment definition read by
    /// <paramref name="read"/> (a game path's bytes, or null), with its weather table's first condition blended in.
    /// </summary>
    public static WorldLighting Of(Cr2wFile file, RedObject world, Func<string, byte[]?> read)
    {
        var parameters = world["environmentParameters"] as RedObject;
        var trajectory = parameters?["globalLightingTrajectory"] as RedObject;
        float bearing = trajectory?.Float("yawDegrees") ?? 0;
        string? path = parameters?["environmentDefinition"] is RedHandle h ? file.PathOf(h) : null;
        string? scenes = parameters?["scenesEnvironmentDefinition"] is RedHandle s ? file.PathOf(s) : null;
        string? table = parameters?["weatherTemplate"] is RedHandle t ? file.PathOf(t) : null;
        var weathers = table is not null && read(table) is { } csv ? ReadWeatherTable(csv) : [];
        var speedTree = parameters?["speedTreeParameters"] as RedObject;
        var lighting = new WorldLighting(bearing, trajectory, path, scenes, table, weathers, null, null, 1, null, EnvironmentParameters.Outdoors, read, null)
        {
            AlphaScalars = new Vector3(speedTree?.Float("alphaScalar3d") ?? 1, speedTree?.Float("alphaScalarGrass") ?? 1, speedTree?.Float("alphaScalarBillboards") ?? 1),
        };
        return weathers.Count > 0 ? lighting.WithWeather(weathers[0].Name) : lighting;
    }

    /// <summary>This lighting with another of the world's weather conditions blended in at once (null for none).</summary>
    public WorldLighting WithWeather(string? name) => With(weather: Weathers.Any(w => w.Name == name) ? name : null, previous: null, transition: 1, keepWeather: false);

    /// <summary>This lighting on its way from the weather now to <paramref name="name"/>, <paramref name="transition"/> of the way (0 to 1).</summary>
    public WorldLighting TowardsWeather(string? name, float transition) =>
        With(weather: name, previous: Weather, transition: transition, keepWeather: false);

    /// <summary>This lighting with its weather change <paramref name="transition"/> of the way through (1 finishes it).</summary>
    public WorldLighting WithTransition(float transition) =>
        transition >= 1 ? With(weather: Weather, previous: null, transition: 1, keepWeather: false) : With(transition: transition);

    /// <summary>This lighting with an environment forced over everything (REDkit's Force preset), or none.</summary>
    public WorldLighting WithPreset(string? path) => With(preset: path, keepPreset: false);

    /// <summary>This lighting using each environment's outdoor, interior or cave parameters.</summary>
    public WorldLighting WithParameters(EnvironmentParameters parameters) => With(parameters: parameters);

    /// <summary>A day with no world's settings: the sun rising in the east and setting in the west, the engine's colours.</summary>
    public static WorldLighting Default { get; } = new(90, null, null, null, null, [], null, null, 1, null, EnvironmentParameters.Outdoors, null, null);

    /// <summary>The environments mixed in now, as REDkit's Env List shows them, highest priority last.</summary>
    public IReadOnlyList<ActiveEnvironment> Active
    {
        get
        {
            var list = new List<ActiveEnvironment>();
            float weather = WeatherWeight(Weather) * (PreviousWeather is null ? 1 : Transition), previous = PreviousWeather is null ? 0 : WeatherWeight(PreviousWeather) * (1 - Transition);
            if (Environment is not null) list.Add(new ActiveEnvironment(0, Preset is null ? Math.Max(0, 1 - weather - previous) : 0, Environment, false));
            if (PreviousWeather is not null && EnvOf(PreviousWeather) is { } p) list.Add(new ActiveEnvironment(250, Preset is null ? previous : 0, p, false));
            if (EnvOf(Weather) is { } w) list.Add(new ActiveEnvironment(250, Preset is null ? weather : 0, w, false));
            if (ScenesEnvironment is not null) list.Add(new ActiveEnvironment(1024, 0, ScenesEnvironment, false));
            if (Preset is not null) list.Add(new ActiveEnvironment(int.MaxValue, 1, Preset, true));
            return list;
        }
    }

    private string? EnvOf(string? weather) => Weathers.FirstOrDefault(w => w.Name == weather)?.Environment;

    private float WeatherWeight(string? weather) => Weathers.FirstOrDefault(w => w.Name == weather) is { Environment: not null } w ? w.Blend : 0;

    private Settings Layer(string? path) =>
        path is null || _read is null ? Empty : _cache.GetOrAdd((path, Parameters), key => Read(key.Item1, _read, key.Item2));

    private static readonly Settings Empty = new();

    private static Settings Read(string? path, Func<string, byte[]?> read, EnvironmentParameters parameters)
    {
        var settings = new Settings();
        if (path is null) return settings;
        try
        {
            if (read(path) is not { } data) return settings;
            var env = EnvironmentFile.Read(data, parameters switch
            {
                EnvironmentParameters.Interiors => "envParamsInteriors",
                EnvironmentParameters.Caves => "envParamsCaves",
                _ => "envParams",
            });
            foreach (var setting in env.Root.All())
            {
                if (!setting.Set) continue;
                if (setting.Curve is { } curve) settings.Curves[setting.Path] = curve;
                else if (setting.Value is bool on) settings.Switches[setting.Path] = on;
                else if (setting.Value is RedHandle handle && env.File.PathOf(handle) is { } file) settings.Files[setting.Path] = file;
            }
        }
        catch (InvalidDataException) { }
        return settings;
    }

    /// <summary>The two balance maps (colour lookup pictures) the grading blends between, the strongest environment's that names them.</summary>
    public (string? First, string? Second) BalanceMaps => (File("m_finalColorBalance › balanceMap0"), File("m_finalColorBalance › balanceMap1"));

    /// <summary>A file setting: the forced preset's, else the weather's (the one most blended in), else the world's.</summary>
    private string? File(string path)
    {
        foreach (var layer in Order())
            if (layer.Files.TryGetValue(path, out var f)) return f;
        return null;
    }

    /// <summary>The layers from the one that wins to the one under everything, for settings that can't be mixed (switches and files).</summary>
    private IEnumerable<Settings> Order()
    {
        if (Preset is not null) yield return Layer(Preset);
        bool towards = PreviousWeather is null || Transition >= 0.5f;
        if (WeatherWeight(towards ? Weather : PreviousWeather) > 0) yield return Layer(EnvOf(towards ? Weather : PreviousWeather));
        yield return Layer(Environment);
    }

    /// <summary>The sky at an hour (0 to 24).</summary>
    public SkyAt Sky(float hour)
    {
        float t = ((hour % 24) + 24) % 24 / 24;
        float sunHeight = _sunHeight?.At(t) ?? MathF.Sin((t - 0.25f) * MathF.Tau) * 0.4f;
        float moonHeight = _moonHeight?.At(t) ?? -sunHeight;
        float sunBearing = _bearing + 180 + (t - 0.5f) * 360;
        const string Sky = "m_sky › ";
        return new SkyAt(
            Colour(Sky + "skyColor", t, new Vector3(0.01f, 0.08f, 0.2f)),
            Colour(Sky + "skyColorHorizon", t, new Vector3(0.02f)),
            // the colours round the sun and the moon at the horizon, scaled as the environment's horizontal scales say
            Colour(Sky + "sunColorHorizon", t, new Vector3(0.8f)) * Value(Sky + "sunColorHorizonHorizontalScale", t, 1),
            Colour(Sky + "sunBackHorizonColor", t, new Vector3(0.2f, 0.3f, 0.5f)),
            Colour(Sky + "sunColorSky", t, new Vector3(0.2f)) * Value(Sky + "sunColorSkyBrightness", t, 1),
            Colour(Sky + "moonColorHorizon", t, new Vector3(0.5f)) * Value(Sky + "moonColorHorizonHorizontalScale", t, 1),
            Colour(Sky + "moonBackHorizonColor", t, new Vector3(0.3f)),
            Colour(Sky + "moonColorSky", t, new Vector3(0.5f)) * MathF.Max(0, Value(Sky + "moonColorSkyBrightness", t, 0)),
            Direction(sunBearing, sunHeight), Direction(sunBearing + 180, moonHeight),
            Value(Sky + "sunAreaSkySize", t, 100), Value(Sky + "moonAreaSkySize", t, 30),
            Value(Sky + "sunInfluence", t, 1), Value(Sky + "moonInfluence", t, 0),
            Value(Sky + "globalSkyBrightness", t, 8), Value(Sky + "visibleSkyBrightnessScale", t, 1), Value(Sky + "horizonVerticalAttenuation", t, 1.5f),
            Value("m_globalFog › fogSkyVertDensityLightFrontScale", t, 1), Value("m_globalFog › fogSkyVertDensityLightBackScale", t, 1),
            // how bright the sky is in the environment probes that reflections come from
            Value("m_ibl › skyBrightnessScale", t, 1));
    }

    /// <summary>
    /// How the sun's light changes colour with distance, as the engine's CalcGlobalLightColor takes it: far off it's
    /// the light side's colour high up and the opposite side's low down; the parameters scale and bias the distance
    /// across (from the centre area over the sides' margin) and the height (from the bottom height to the top).
    /// </summary>
    public (Vector3 Side, Vector3 OppositeSide, Vector4 Parameters) GlobalLightColour(float hour)
    {
        float t = ((hour % 24) + 24) % 24 / 24;
        const string Light = "m_globalLight › ";
        float centre = Value(Light + "sunColorCenterArea", t, 1e6f), margin = Math.Max(1, Value(Light + "sunColorSidesMargin", t, 1));
        float bottom = Value(Light + "sunColorBottomHeight", t, 0), top = Value(Light + "sunColorTopHeight", t, 100);
        float rise = Math.Max(1, top - bottom);
        return (Colour(Light + "sunColorLightSide", t, Colour(Light + "sunColor", t, Vector3.One)),
            Colour(Light + "sunColorLightOppositeSide", t, Colour(Light + "sunColor", t, Vector3.One)),
            new Vector4(1 / margin, -centre / margin, 1 / rise, -bottom / rise));
    }

    /// <summary>The plants' shading and the ambient light at an hour (0 to 24).</summary>
    public FoliageAt Foliage(float hour)
    {
        float t = ((hour % 24) + 24) % 24 / 24;
        const string Tree = "m_speedTree › ", Light = "m_globalLight › ", Ibl = "m_ibl › ";
        Vector4[] Random(string kind) => [.. Enumerable.Range(0, 3).Select(i =>
            // a tint: from white towards the colour as stored by its strength (the engine's shader takes 1 minus it from
            // the constants). The C++ that fills them isn't in REDkit; of the readings tried this one matches REDkit's
            // trees and grass over White Orchard (linear or plain multipliers came out two to three times too dark)
            new Vector4(Vector3.Max(Vector3.Zero, Vector3.Lerp(Vector3.One, Raw(Tree + kind + " › randomColor" + i, t, new Vector3(255)) / 255,
                Setting(Tree + kind + " › randomColor" + i, t)?.W ?? 0)),
                Value(Tree + kind + " › saturation" + i, t, 1)))];
        Vector3 Weights(string kind) => Raw(Tree + kind + " › luminanceWeights", t, new Vector3(128)) / 255;
        return new FoliageAt(
            Random("randomColorsTrees"), Weights("randomColorsTrees"), Random("randomColorsBranches"), Weights("randomColorsBranches"),
            Random("randomColorsGrass"), Weights("randomColorsGrass"),
            Colour(Tree + "diffuse", t, Vector3.One), Value(Tree + "ambientOcclusionScale", t, 1), Colour(Tree + "billboardsColor", t, Vector3.One),
            Value(Tree + "translucencyScale", t, 1),
            new Vector4(Value(Light + "translucencyViewDependency", t, 0.5f), Value(Light + "translucencyBaseFlatness", t, 1),
                Value(Light + "translucencyFlatBrightness", t, 1), Value(Light + "translucencyGainBrightness", t, 1)),
            Value(Light + "envProbeAmbientScaleLight", t, 1), Value(Light + "envProbeAmbientScaleShadow", t, 1),
            Colour(Light + "envProbeBaseLightingAmbient › colorAmbient", t, Vector3.One),
            Value(Ibl + "skyBrightnessScale", t, 1), Value(Ibl + "skySaturationScale", t, 1), Value(Ibl + "groundBrightnessScale", t, 1),
            Value(Ibl + "groundSaturationScale", t, 1), Value(Ibl + "maxSkyBrightness", t, 1000));
    }

    /// <summary>The environment probes' settings at an hour (0 to 24).</summary>
    public ProbesAt Probes(float hour)
    {
        float t = ((hour % 24) + 24) % 24 / 24;
        const string Base = "m_globalLight › envProbeBaseLightingAmbient › ", Light = "m_globalLight › ", Ao = "m_ssaoNV › ", Pbr = "m_globalPBRAdjustments › ";
        return new ProbesAt(Colour(Base + "colorSkyTop", t, Vector3.One), Colour(Base + "colorSkyHorizon", t, Vector3.One), Value(Base + "skyShape", t, 1),
            Colour(Base + "colorSceneAdd", t, Vector3.Zero),
            Value(Light + "envProbeReflectionScaleLight", t, 1), Value(Light + "envProbeReflectionScaleShadow", t, 1),
            Value(Ao + "radius", t, 1), Value(Ao + "bias", t, 0.1f), Value(Ao + "powerExponent", t, 2), Value(Ao + "nonAmbientInfluence", t, 0.5f),
            Value(Pbr + "specularF90Grass", t, 1), Value(Pbr + "specularF90OtherFoliage", t, 1), Value("m_speedTree › specularScale", t, 1),
            Value("m_water › waterFresnel", t, 0.02f));
    }

    /// <summary>The tone mapping and colour grading at an hour (0 to 24).</summary>
    public GradingAt Grading(float hour)
    {
        float t = ((hour % 24) + 24) % 24 / 24;
        const string Tone = "m_toneMapping › ", Agx = "m_toneMapping › agxToneMapParameters › ", Balance = "m_finalColorBalance › ";
        Vector4 Part(string name) => new(Raw(Balance + name + " › color", t, Vector3.One * 255) / 255, Value(Balance + name + " › saturation", t, 1));
        return new GradingAt(
            Value(Tone + "exposureScale", t, 1), Value(Tone + "luminanceLimitShape", t, 0.5f),
            Value(Tone + "luminanceLimitMin", t, 0.41f), Value(Tone + "luminanceLimitMax", t, 1.7f),
            Value(Agx + "agxGlobalContrastPower", t, 2.5f), Value(Agx + "agxShoulderContrastPower", t, 3.2f),
            Value(Agx + "agxToeContrastPower", t, 2.2f), Value(Agx + "agxBlackPointLift", t, 0.04f),
            Raw(Agx + "agxSlopeColor", t, Vector3.One), Raw(Agx + "agxPowerColor", t, Vector3.One),
            Value(Agx + "agxSaturationScale", t, 1), Value(Agx + "agxMidGray", t, 0.18f),
            Switch(Balance + "activated") != false && Switch(Balance + "activatedBalanceMap") == true,
            Value(Balance + "balanceMapAmount", t, 0), Value(Balance + "balanceMapLerp", t, 0), Value(Balance + "balancePostBrightness", t, 1),
            Switch(Balance + "activated") != false && Switch(Balance + "activatedParametricBalance") == true,
            Value(Balance + "levelsShadows", t, 0), Value(Balance + "levelsMidtones", t, 1), Value(Balance + "levelsHighlights", t, 1),
            Part("parametricBalanceLow"), Part("parametricBalanceMid"), Part("parametricBalanceHigh"),
            Value(Balance + "midtoneRangeMin", t, 0.2f), Value(Balance + "midtoneRangeMax", t, 0.2f),
            Value(Balance + "midtoneMarginMin", t, 0.1f), Value(Balance + "midtoneMarginMax", t, 0.1f),
            Value(Tone + "skyLuminanceCustomValue", t, 1), Value(Tone + "skyLuminanceCustomAmount", t, 0),
            // the vignette's colours as stored (the final pass works on the picture as it's shown): 0 to 1
            Raw(Balance + "vignetteWeights", t, Vector3.Zero) / 255, Raw(Balance + "vignetteColor", t, Vector3.Zero) / 255,
            Value(Balance + "vignetteOpacity", t, 0));
    }

    private bool? Switch(string path)
    {
        foreach (var layer in Order())
            if (layer.Switches.TryGetValue(path, out var on)) return on;
        return null;
    }

    /// <summary>A colour setting's three values as they're stored, not made linear.</summary>
    private Vector3 Raw(string path, float t, Vector3 otherwise) => Setting(path, t) is { } v ? new Vector3(v.X, v.Y, v.Z) : otherwise;

    /// <summary>A weather table's rows (a semicolon-separated 2D array with a header): each condition's name, environment, blend, wind and timings.</summary>
    private static List<WeatherCondition> ReadWeatherTable(byte[] csv)
    {
        var rows = new List<WeatherCondition>();
        var lines = Encoding.UTF8.GetString(csv).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count < 2) return rows;
        var header = lines[0].Split(';');
        int name = Array.IndexOf(header, "name"), env = Array.IndexOf(header, "envPath"), blend = Array.IndexOf(header, "envBlend"),
            wind = Array.IndexOf(header, "windScale"), probability = Array.IndexOf(header, "probability"), blendTime = Array.IndexOf(header, "blendTime"),
            occurrence = Array.IndexOf(header, "occurenceTime");
        if (name < 0) return rows;
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(';');
            string Cell(int i) => i >= 0 && i < cells.Length ? cells[i].Trim() : "";
            if (Cell(name).Length == 0) continue;
            float Number(int i, float otherwise) =>
                float.TryParse(Cell(i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : otherwise;
            rows.Add(new WeatherCondition(Cell(name), Cell(env).Length > 0 ? Cell(env) : null, Number(blend, 1), Number(wind, 0.2f),
                Number(probability, 0), Number(blendTime, 120), Number(occurrence, 600)));
        }
        return rows;
    }

    /// <summary>The lighting at an hour (0 to 24).</summary>
    public LightingAt At(float hour)
    {
        float t = ((hour % 24) + 24) % 24 / 24;
        float sunHeight = _sunHeight?.At(t) ?? MathF.Sin((t - 0.25f) * MathF.Tau) * 0.4f;
        float moonHeight = _moonHeight?.At(t) ?? -sunHeight;
        float lightHeight = _lightHeight?.At(t) ?? MathF.Max(0.05f, sunHeight);
        bool moon = (_lightChoice?.At(t) ?? (sunHeight > 0 ? 1 : 0)) < 0.5f;
        // the sun goes round once a day, opposite the trajectory's bearing at noon, its bearing growing through the day:
        // measured from REDkit's shadows over White Orchard (north-north-east at 09:10, north-north-west at 07:00); the
        // moon opposite it
        float sunBearing = _bearing + 180 + (t - 0.5f) * 360;
        var sun = Direction(sunBearing, sunHeight);
        var light = Direction(moon ? sunBearing + 180 : sunBearing, lightHeight);
        float vertical = Value("m_globalFog › fogVertDensity", t, -0.02f);

        return new LightingAt(
            light, moon ? Direction(sunBearing + 180, moonHeight) : sun, moon,
            Colour("m_globalLight › sunColor", t, new Vector3(1, 0.95f, 0.85f) * 60),
            // the ambient light where the sun reaches, scaled as the environment's probes are
            Colour("m_globalLight › envProbeBaseLightingAmbient › colorAmbient", t, Vector3.One * 3) * Value("m_globalLight › envProbeAmbientScaleLight", t, 1),
            Colour("m_sky › skyColor", t, new Vector3(0.1f, 0.3f, 0.6f)),
            Colour("m_sky › skyColorHorizon", t, new Vector3(0.6f, 0.7f, 0.8f)),
            Colour("m_sky › sunColorSky", t, new Vector3(1, 0.9f, 0.7f)),
            Value("m_sky › sunAreaSkySize", t, 100),
            Value("m_sky › globalSkyBrightness", t, 8),
            Colour("m_globalFog › fogColorFront", t, new Vector3(0.6f, 0.7f, 0.8f) * 10),
            Colour("m_globalFog › fogColorMiddle", t, new Vector3(0.5f, 0.6f, 0.7f) * 9),
            Colour("m_globalFog › fogColorBack", t, new Vector3(0.45f, 0.55f, 0.7f) * 8),
            Value("m_globalFog › fogDensity", t, 0.001f),
            Value("m_globalFog › fogAppearDistance", t, 1),
            Value("m_globalFog › fogAppearRange", t, 66),
            vertical,
            Value("m_globalFog › fogVertOffset", t, 40),
            Value("m_globalFog › fogVertDensityLightFront", t, 1),
            Value("m_globalFog › fogVertDensityLightBack", t, 1),
            Value("m_globalFog › fogFinalExp", t, 1),
            Colour("m_globalFog › aerialColorFront", t, Vector3.One),
            Colour("m_globalFog › aerialColorMiddle", t, Vector3.One),
            Colour("m_globalFog › aerialColorBack", t, Vector3.One),
            Value("m_globalFog › aerialFinalExp", t, 30),
            Colour("m_globalFog › fogCustomColor", t, Vector3.One * 5),
            Value("m_globalFog › fogCustomAmountScale", t, 1),
            Value("m_globalFog › fogCustomColorStart", t, 1),
            Value("m_globalFog › fogCustomColorRange", t, 1),
            Value("m_globalFog › fogCustomAmountScaleStart", t, 1),
            Value("m_globalFog › fogCustomAmountScaleRange", t, 1),
            Value("m_globalFog › fogDistClamp", t, 16000),
            Value("m_globalFog › fogSkyDensityScale", t, 1),
            Colour("m_water › waterColor", t, new Vector3(0.12f, 0.14f, 0.07f)),
            Value("m_water › underWaterFogIntensity", t, 0.8f),
            Math.Clamp(_dayAmount?.At(t) ?? Math.Clamp(sunHeight * 4 + 0.5f, 0, 1), 0, 1),
            // how far the eye adapts: the tone mapping's luminance limits
            Value("m_toneMapping › luminanceLimitMin", t, 0.41f), Value("m_toneMapping › luminanceLimitMax", t, 1.7f));
    }

    /// <summary>A direction from a bearing (degrees, clockwise from north) and a height (a quarter turn up at 1).</summary>
    private static Vector3 Direction(float bearing, float height)
    {
        float b = bearing * MathF.PI / 180, e = Math.Clamp(height, -1, 1) * MathF.PI / 2;
        return Vector3.Normalize(new Vector3(MathF.Sin(b) * MathF.Cos(e), MathF.Cos(b) * MathF.Cos(e), MathF.Sin(e)));
    }

    /// <summary>
    /// A setting at a time: the world's environment's and the weather's mixed by the weather's blend, as REDkit's Env
    /// List weighs them (WT_Clear at 100% leaves the world's own at 0%); where only one sets it, that one's.
    /// </summary>
    private Vector4? Setting(string path, float t)
    {
        if (Preset is not null && Layer(Preset).Curves.TryGetValue(path, out var forced)) return forced.Evaluate(t);
        var world = Layer(Environment);
        Vector4? Under(string? weather)
        {
            bool own = world.Curves.TryGetValue(path, out var mine);
            var layer = Layer(EnvOf(weather));
            float blend = WeatherWeight(weather);
            bool theirs = blend > 0 && layer.Curves.TryGetValue(path, out var curve);
            if (own && theirs) return Vector4.Lerp(mine!.Evaluate(t), layer.Curves[path].Evaluate(t), blend);
            if (theirs) return layer.Curves[path].Evaluate(t);
            return own ? mine!.Evaluate(t) : null;
        }
        var now = Under(Weather);
        if (PreviousWeather is null || Transition >= 1) return now;
        var before = Under(PreviousWeather);
        if (now is { } a && before is { } b) return Vector4.Lerp(b, a, Transition);
        return now ?? before;
    }

    // the settings are found by their paths as EnvironmentFile writes them, groups and names joined by " › "
    private float Value(string path, float t, float otherwise) => Setting(path, t)?.X ?? otherwise;

    /// <summary>A colour setting at a time, linear: its 0 to 255 values taken as sRGB, times its strength.</summary>
    private Vector3 Colour(string path, float t, Vector3 otherwise)
    {
        if (Setting(path, t) is not { } v) return otherwise;
        static float Linear(float c) => MathF.Pow(Math.Clamp(c / 255, 0, 1), 2.2f);
        return new Vector3(Linear(v.X), Linear(v.Y), Linear(v.Z)) * v.W;
    }
}
