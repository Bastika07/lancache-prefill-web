using System.Globalization;

namespace LancachePrefill.Web;

/// <summary>How the Job pages write sizes, durations and speeds.</summary>
public static class JobFormat
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Format(Culture, "{0:F1} GiB", bytes / (double)(1L << 30)),
        >= 1L << 20 => string.Format(Culture, "{0:F1} MiB", bytes / (double)(1L << 20)),
        >= 1L << 10 => string.Format(Culture, "{0:F1} KiB", bytes / (double)(1L << 10)),
        _ => string.Format(Culture, "{0} B", bytes),
    };

    public static string Speed(long bytesPerSecond) => string.Format(Culture, "{0:F1} MB/s", bytesPerSecond / 1_000_000.0);

    public static string Duration(TimeSpan duration) => duration.TotalHours >= 1
        ? string.Format(Culture, "{0}h {1:D2}m", (int)duration.TotalHours, duration.Minutes)
        : duration.TotalMinutes >= 1
            ? string.Format(Culture, "{0}m {1:D2}s", (int)duration.TotalMinutes, duration.Seconds)
            : string.Format(Culture, "{0}s", (int)duration.TotalSeconds);

    public static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Culture);

    public static int Percent(long done, long total) => total > 0 ? (int)(done * 100 / total) : 0;
}
