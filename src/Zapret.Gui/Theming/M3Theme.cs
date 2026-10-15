using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace Zapret.Gui.Theming;

public static class M3Theme
{
    // Material 3 baseline scheme. Roles, rather than opacity, define tonal hierarchy.
    private static readonly (string Role, string Light, string Dark)[] Colors =
    [
        ("Primary", "#6750A4", "#D0BCFF"), ("OnPrimary", "#FFFFFF", "#381E72"),
        ("PrimaryContainer", "#EADDFF", "#4F378B"), ("OnPrimaryContainer", "#21005D", "#EADDFF"),
        ("Secondary", "#625B71", "#CCC2DC"), ("OnSecondary", "#FFFFFF", "#332D41"),
        ("SecondaryContainer", "#E8DEF8", "#4A4458"), ("OnSecondaryContainer", "#1D192B", "#E8DEF8"),
        ("Surface", "#FEF7FF", "#141218"), ("OnSurface", "#1D1B20", "#E6E0E9"),
        ("OnSurfaceVariant", "#49454F", "#CAC4D0"),
        ("SurfaceContainerLowest", "#FFFFFF", "#0F0D13"),
        ("SurfaceContainerLow", "#F7F2FA", "#1D1B20"),
        ("SurfaceContainer", "#F3EDF7", "#211F26"),
        ("SurfaceContainerHigh", "#ECE6F0", "#2B2930"),
        ("SurfaceContainerHighest", "#E6E0E9", "#36343B"),
        ("Outline", "#79747E", "#938F99"), ("OutlineVariant", "#CAC4D0", "#49454F"),
        ("Error", "#B3261E", "#F2B8B5"), ("OnError", "#FFFFFF", "#601410"),
        ("ErrorContainer", "#F9DEDC", "#8C1D18"), ("OnErrorContainer", "#410E0B", "#F9DEDC"),
        ("InverseSurface", "#322F35", "#E6E0E9"), ("InverseOnSurface", "#F5EFF7", "#322F35"),
        // Semantic extensions: success and warning are not standard M3 color roles.
        ("Success", "#286A48", "#96D5AC"), ("SuccessContainer", "#B2F2C7", "#0C5131"),
        ("Warning", "#795900", "#F4C653"), ("WarningContainer", "#FFDF90", "#5C4300")
    ];

    public static void Apply(bool dark)
    {
        var palette = new PaletteHelper();
        var theme = palette.GetTheme();
        theme.SetBaseTheme(dark ? BaseTheme.Dark : BaseTheme.Light);
        theme.SetPrimaryColor(Parse(dark ? "#D0BCFF" : "#6750A4"));
        theme.SetSecondaryColor(Parse(dark ? "#CCC2DC" : "#625B71"));
        palette.SetTheme(theme);
        var resources = Application.Current.Resources;
        foreach (var (role, light, night) in Colors)
        {
            var brush = new SolidColorBrush(Parse(dark ? night : light));
            brush.Freeze();
            resources["M3." + role] = brush;
        }
        // Align toolkit popups, text fields, switch and snackbar with the same roles.
        foreach (var (key, role) in new (string, string)[]
        {
            ("Background", "Surface"), ("Foreground", "OnSurface"), ("Paper", "SurfaceContainerLow"),
            ("Card.Background", "SurfaceContainerLow"), ("Body", "OnSurface"), ("Body.Light", "OnSurfaceVariant"),
            ("Primary", "Primary"), ("Primary.Foreground", "OnPrimary"),
            ("Secondary", "Secondary"), ("Secondary.Foreground", "OnSecondary"),
            ("ValidationError", "Error"), ("Divider", "OutlineVariant")
        }) resources["MaterialDesign.Brush." + key] = resources["M3." + role];
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
