using System.Globalization;

namespace DiskTemizleyici.Core;

public static class ByteFormat
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    /// <summary>1536 → "1,5 KB" (with the current culture's decimal separator).</summary>
    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        double value = Math.Abs(bytes);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var sign = bytes < 0 ? "-" : "";
        var format = unit == 0 ? "0" : value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return sign + value.ToString(format, culture) + " " + Units[unit];
    }
}
