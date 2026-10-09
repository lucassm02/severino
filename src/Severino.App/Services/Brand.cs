using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace Severino.App.Services;

/// <summary>Colours taken from the mascot (assets/branding/severino-rosto.png).</summary>
public static class Brand
{
    public static readonly Color Navy = Color.FromRgb(0x01, 0x1D, 0x41);
    public static readonly Color CapBlue = Color.FromRgb(0x01, 0x31, 0x66);
    public static readonly Color Orange = Color.FromRgb(0xEE, 0x8F, 0x3B);

    /// <summary>
    /// The cap blue, lifted for dark backgrounds: the artwork's #013166 turns links and
    /// accent buttons too dim to read on a dark window.
    /// </summary>
    public static readonly Color CapBlueOnDark = Color.FromRgb(0x5B, 0x9B, 0xEF);

    /// <summary>Uses the brand blue as accent instead of the Windows accent colour.</summary>
    public static void ApplyAccent(ApplicationTheme theme) =>
        ApplicationAccentColorManager.Apply(theme == ApplicationTheme.Dark ? CapBlueOnDark : CapBlue, theme);
}
