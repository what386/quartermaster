using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Quartermaster.Gui.Services;

public enum ThemePreset { Dark, Slate, Light }

/// <summary>Applies saved palettes and settings previews to the app and Fluent controls.</summary>
public sealed class ThemeManager
{
    public const string DefaultAccent = "#397ADA";
    private (ThemePreset Theme, string Accent)? loaded;

    public void Load(ApplicationSettings settings)
    {
        var appearance = (settings.Theme, settings.AccentColor);
        // Unrelated library/profile refreshes must not undo an unsaved preview.
        if (loaded == appearance) return;
        loaded = appearance;
        Apply(settings.Theme, Color.Parse(settings.AccentColor));
    }

    public static void Validate(ThemePreset theme, string? accent)
    {
        if (!Enum.IsDefined(theme)) throw new InvalidDataException("Invalid theme setting.");
        if (!Color.TryParse(accent, out var color) || color.A != 255)
            throw new InvalidDataException("Accent color must be an opaque color, such as #397ADA.");
    }

    public static string Format(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static void Apply(ThemePreset preset, Color accent)
    {
        var app = Application.Current;
        if (app is null) return;
        var light = preset == ThemePreset.Light;
        var palette = preset switch
        {
            ThemePreset.Dark => new Palette("#191B1E", "#101214", "#202327", "#34383D", "#9299A1", "#FFFFFF",
                "#17191C", "#1D1F23", "#25282C", "#141619", "#454B52", "#24272B", "#6F767E", "#41464D", "#5C6269"),
            ThemePreset.Slate => new Palette("#1A2230", "#121925", "#222D3E", "#3A4960", "#A3B2C8", "#EDF3FC",
                "#17202D", "#1E2939", "#29364A", "#141C28", "#53647D", "#29364A", "#8294AE", "#4A5C75", "#647893"),
            ThemePreset.Light => new Palette("#F3F4F6", "#E6E8EC", "#FFFFFF", "#CFD3DA", "#535D6A", "#171B22",
                "#F7F8FA", "#FFFFFF", "#E4E7EB", "#FFFFFF", "#A6AFBA", "#E8EBEF", "#697482", "#A6AFBA", "#8A95A3"),
            _ => throw new ArgumentOutOfRangeException(nameof(preset))
        };
        void Brush(string key, string value) => app.Resources[key] = new SolidColorBrush(Color.Parse(value));
        Brush("AppBackground", palette.Background); Brush("SidebarBackground", palette.Sidebar);
        Brush("CardBackground", palette.Card); Brush("LineBrush", palette.Line);
        Brush("MutedBrush", palette.Muted); Brush("TextBrush", palette.Text);
        Brush("ListBackground", palette.List); Brush("RowBackground", palette.Row);
        Brush("TableHeaderBackground", palette.Header); Brush("InputBackground", palette.Input);
        Brush("ProfileBorderBrush", palette.ProfileBorder); Brush("TileBackground", palette.Tile);
        Brush("DragHandleBrush", palette.Handle); Brush("SwitchBackground", palette.Switch);
        Brush("TileBorderBrush", palette.TileBorder);
        Brush("SuccessBrush", light ? "#16805C" : "#4AB98A");
        Brush("ClashBrush", light ? "#907000" : "#E2C457");
        Brush("DisabledBrush", light ? "#C93434" : "#E56A6A");
        Brush("ErrorBrush", light ? "#A52626" : "#F3A5A5");
        Brush("SwitchKnobBrush", "#111315");
        Brush("OverlayBrush", "#A6000000");
        app.Resources["AccentBrush"] = new SolidColorBrush(accent);
        // Pick readable text for both very pale and very dark custom accents.
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Linear(accent.R) + 0.7152 * Linear(accent.G) + 0.0722 * Linear(accent.B);
        Brush("AccentForegroundBrush", luminance > 0.179 ? "#101214" : "#FFFFFF");
        static Color Blend(Color background, Color foreground, double amount) => Color.FromRgb(
            (byte)Math.Round(background.R * (1 - amount) + foreground.R * amount),
            (byte)Math.Round(background.G * (1 - amount) + foreground.G * amount),
            (byte)Math.Round(background.B * (1 - amount) + foreground.B * amount));
        var original = preset == ThemePreset.Dark && Format(accent) == "#F27A22";
        Brush("SelectedBackground", original ? "#34291F" : Format(Blend(Color.Parse(palette.Row), accent, 0.15)));
        Brush("SelectedRowBackground", original ? "#2B2723" : Format(Blend(Color.Parse(palette.Row), accent, 0.10)));
        Brush("SelectedProfileBackground", original ? "#30261F" : Format(Blend(Color.Parse(palette.Card), accent, 0.12)));

        var variant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var fluent = app.Styles.OfType<FluentTheme>().FirstOrDefault();
        if (fluent is not null)
        {
            if (!fluent.Palettes.TryGetValue(variant, out var controls))
                fluent.Palettes[variant] = controls = new ColorPaletteResources();
            controls.Accent = accent;
            controls.RegionColor = Color.Parse(palette.Background);
            controls.ChromeLow = Color.Parse(palette.Sidebar);
            controls.ChromeMedium = Color.Parse(original ? "#23262A" : palette.Header);
        }
        app.RequestedThemeVariant = variant;
    }

    private sealed record Palette(string Background, string Sidebar, string Card, string Line, string Muted, string Text,
        string List, string Row, string Header, string Input, string ProfileBorder, string Tile, string Handle, string Switch, string TileBorder);
}
