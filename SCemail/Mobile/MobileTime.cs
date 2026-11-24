namespace SCemail.Mobile;

/// <summary>Conversioni UTC ↔ ora italiana per la consultazione (il DB salva in UTC).</summary>
public static class MobileTime
{
    private static readonly TimeZoneInfo Rome = Resolve();

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "Europe/Rome", "W. Europe Standard Time" })
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        return TimeZoneInfo.Local;
    }

    public static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Rome);

    public static DateTime ToUtc(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Rome);

    public static DateTime TodayLocal => ToLocal(DateTime.UtcNow).Date;
}
