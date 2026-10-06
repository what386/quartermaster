using Avalonia.Media;
using Quartermaster.Gui.Services;

namespace Quartermaster.Gui.Settings;

public sealed partial class SettingsViewModel
{
    private int themeChoice;
    private Color accentColor = Color.Parse(ThemeManager.DefaultAccent);
    public IReadOnlyList<string> ThemeChoices { get; } = ["Dark", "Slate", "Light"];
    public int ThemeChoice
    {
        get => themeChoice;
        set { if (Set(ref themeChoice, value)) PreviewAppearance(); }
    }
    public Color AccentColor
    {
        get => accentColor;
        set { if (Set(ref accentColor, Color.FromRgb(value.R, value.G, value.B))) PreviewAppearance(); }
    }
    public bool ShowAppearance => Matches("App settings Appearance theme palette accent color dark slate light");
    private bool ValidAppearance => ThemeChoice >= 0 && ThemeChoice < ThemeChoices.Count;
    private bool AppearanceChanged => ThemeChoice != (int)Session.Settings.Theme ||
        ThemeManager.Format(AccentColor) != ThemeManager.Format(Color.Parse(Session.Settings.AccentColor));
    private void PreviewAppearance()
    {
        if (ValidAppearance) ThemeManager.Apply((ThemePreset)ThemeChoice, AccentColor);
        SaveCommand.Refresh();
    }
    private void LoadAppearance()
    {
        ThemeChoice = (int)Session.Settings.Theme;
        AccentColor = Color.Parse(Session.Settings.AccentColor);
    }
    private void RefreshAppearance(ApplicationSettings previous)
    {
        if (ThemeChoice == (int)previous.Theme) ThemeChoice = (int)Session.Settings.Theme;
        if (ThemeManager.Format(AccentColor) == ThemeManager.Format(Color.Parse(previous.AccentColor)))
            AccentColor = Color.Parse(Session.Settings.AccentColor);
    }
}
