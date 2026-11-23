using System.Globalization;

namespace KanbanHub.Web.State;

/// <summary>
/// Picks black or white text for a colored chip, whichever contrasts more (WCAG relative luminance).
/// One of the two always reaches at least 4.58:1, so text on any palette color meets AA.
/// </summary>
public static class ChipText
{
    private const string Black = "#000000";
    private const string White = "#FFFFFF";

    public static string On(string background) =>
        Contrast(background, Black) >= Contrast(background, White) ? Black : White;

    /// <summary>The contrast the chosen text color gives on this background.</summary>
    public static double ContrastOn(string background) => Contrast(background, On(background));

    /// <summary>From 1 (no contrast) to 21 (black on white).</summary>
    public static double Contrast(string first, string second)
    {
        double a = Luminance(first);
        double b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string color) =>
        (0.2126 * Channel(color, 1)) + (0.7152 * Channel(color, 3)) + (0.0722 * Channel(color, 5));

    private static double Channel(string color, int start)
    {
        if (color.Length != 7 || !int.TryParse(color.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
            return 0;

        double share = value / 255.0;
        return share <= 0.03928 ? share / 12.92 : Math.Pow((share + 0.055) / 1.055, 2.4);
    }
}
