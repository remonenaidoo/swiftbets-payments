namespace SwiftBets.Payments.Domain;

/// <summary>Reconciliation days are South African calendar days (UTC+2, no daylight saving).</summary>
public static class PaymentCalendar
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(2);

    public static DateOnly DayOf(DateTimeOffset at) => DateOnly.FromDateTime(at.ToOffset(Offset).DateTime);

    public static (DateTimeOffset From, DateTimeOffset To) Bounds(DateOnly day)
    {
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), Offset);
        return (from, from.AddDays(1));
    }
}
