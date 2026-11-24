using System.Windows;
using System.Windows.Media;

namespace Winnas.Console.Infrastructure;

/// <summary>
/// One selectable skin. The swatch brushes are sampled from the palette file
/// itself, so a skin's preview can never drift away from what it actually looks
/// like.
/// </summary>
public sealed class Skin
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Hint { get; init; }

    public required Brush PageBrush { get; init; }
    public required Brush CardBrush { get; init; }
    public required Brush AccentBrush { get; init; }
    public required Brush TextBrush { get; init; }

    /// <summary>Light or dark, used for the small "浅色/深色" tag.</summary>
    public required bool IsDark { get; init; }

    public string ModeText => IsDark ? "深色" : "浅色";
}

/// <summary>
/// Swaps the colour palette at runtime.
///
/// Every control reads its colours through <c>DynamicResource</c>, so replacing
/// exactly one dictionary — <c>Themes/Palette.*.xaml</c> — repaints the whole
/// app without recreating a single view. Styles, templates and layout live in
/// <c>Styles.xaml</c> and never change.
///
/// The palette must stay at the front of <c>MergedDictionaries</c>: entries
/// merged later win, and Styles.xaml itself references these keys only
/// dynamically, so ordering matters for the initial load as well.
/// </summary>
public static class ThemeManager
{
    public const string DefaultKey = "day";

    private static readonly string[] Order =
        { "day", "night", "ocean", "sand", "violet", "forest" };

    private static readonly Dictionary<string, (string Name, string Hint, bool Dark)> Meta = new()
    {
        ["day"] = ("极简白", "中性浅色，白天长时间看字最省力", false),
        ["night"] = ("深空黑", "暗光环境首选，整屏不刺眼", true),
        ["ocean"] = ("海洋蓝", "冷调蓝灰，看起来冷静专注", false),
        ["sand"] = ("暖砂", "米黄暖调，接近纸张的柔和不反光", false),
        ["violet"] = ("靛紫", "紫调个性，偏设计感", false),
        ["forest"] = ("护眼绿", "低饱和绿，久看不累", false),
    };

    private static IReadOnlyList<Skin>? _skins;

    public static IReadOnlyList<Skin> Skins => _skins ??= Build();

    public static string CurrentKey { get; private set; } = DefaultKey;

    public static Skin Current =>
        Skins.FirstOrDefault(s => s.Key == CurrentKey) ?? Skins[0];

    /// <summary>Raised after the palette has been swapped.</summary>
    public static event EventHandler? Changed;

    /// <summary>
    /// Replaces the palette dictionary in place. Safe to call with an unknown
    /// key — it falls back to the default skin rather than throwing, because a
    /// hand-edited config.json must never stop the app from opening.
    /// </summary>
    public static void Apply(string? key)
    {
        var resolved = Meta.ContainsKey(key ?? "") ? key! : DefaultKey;
        var app = Application.Current;

        if (app is null)
        {
            CurrentKey = resolved;
            return;
        }

        var fresh = Load(resolved);
        var merged = app.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(IsPalette);

        if (existing is null)
        {
            merged.Insert(0, fresh);
        }
        else
        {
            // Remove before inserting: a later entry takes precedence, so an
            // append would leave the old colours winning and the switch would
            // look like it did nothing.
            var index = merged.IndexOf(existing);
            merged.RemoveAt(index);
            merged.Insert(index, fresh);
        }

        CurrentKey = resolved;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Force-applies the palette without touching the saved setting.</summary>
    public static void ApplyTransient(string? key)
    {
        if (Meta.ContainsKey(key ?? "")) Apply(key);
    }

    public static bool IsKnown(string? key) => Meta.ContainsKey(key ?? "");

    private static ResourceDictionary Load(string key) =>
        new() { Source = new Uri($"/Themes/Palette.{key}.xaml", UriKind.Relative) };

    private static bool IsPalette(ResourceDictionary d)
    {
        var src = d.Source?.OriginalString ?? "";
        return src.Contains("Themes/Palette.", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<Skin> Build()
    {
        var list = new List<Skin>(Order.Length);
        foreach (var key in Order)
        {
            var (name, hint, dark) = Meta[key];
            var c = Sample(key);
            list.Add(new Skin
            {
                Key = key,
                Name = name,
                Hint = hint,
                IsDark = dark,
                PageBrush = Frozen(c.Page),
                CardBrush = Frozen(c.Card),
                AccentBrush = Frozen(c.Accent),
                TextBrush = Frozen(c.Text),
            });
        }
        return list;
    }

    private static Brush Frozen(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    /// <summary>Reads four representative colours straight out of the palette file.</summary>
    private static (Color Page, Color Card, Color Accent, Color Text) Sample(string key)
    {
        try
        {
            var dict = Load(key);

            Color Get(string name) =>
                dict.Contains(name) && dict[name] is SolidColorBrush b
                    ? b.Color
                    : Color.FromRgb(0x80, 0x80, 0x80);

            return (Get("PageBgBrush"), Get("CardBgBrush"),
                    Get("PrimaryBrush"), Get("TextMainBrush"));
        }
        catch
        {
            return (Color.FromRgb(0x80, 0x80, 0x80),
                    Color.FromRgb(0x90, 0x90, 0x90),
                    Color.FromRgb(0x70, 0x70, 0x70),
                    Color.FromRgb(0x10, 0x10, 0x10));
        }
    }
}
