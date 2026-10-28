using System.Globalization;

namespace SelectLunch.Shared;

public static class EarlyDepartureDuration
{
    public static string FormatMinutes(int seconds) =>
        (seconds / 60m).ToString("0.####", CultureInfo.InvariantCulture);

    public static bool TryParseMinutes(string? text, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var value = text.AsSpan().Trim();
        if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var minutes) || minutes < 0 || minutes > int.MaxValue / 60m + 1m)
            return false;

        // Decimal parsing can round an extremely small nonzero input to zero.
        if (minutes == 0)
        {
            foreach (var c in value)
                if (c is >= '1' and <= '9')
                    return false;
        }

        var rounded = decimal.Round(minutes * 60m, 0, MidpointRounding.AwayFromZero);
        if (rounded > int.MaxValue || (minutes > 0 && rounded == 0))
            return false;

        seconds = (int)rounded;
        return true;
    }
}
