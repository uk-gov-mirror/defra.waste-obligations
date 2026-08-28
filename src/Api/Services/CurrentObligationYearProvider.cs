namespace Defra.WasteObligations.Api.Services;

public class CurrentObligationYearProvider(TimeProvider timeProvider) : ICurrentObligationYearProvider
{
    private static readonly TimeZoneInfo UnitedKingdomTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public int GetCurrentObligationYear()
    {
        var utcNow = timeProvider.GetUtcNow();
        var localNow = TimeZoneInfo.ConvertTime(utcNow, UnitedKingdomTimeZone);

        return GetCurrentObligationYear(localNow);
    }

    public ObligationYearHandover GetHandover(TimeSpan outgoingYearGracePeriod)
    {
        var utcNow = timeProvider.GetUtcNow();
        var localNow = TimeZoneInfo.ConvertTime(utcNow, UnitedKingdomTimeZone);
        var currentObligationYear = GetCurrentObligationYear(localNow);

        if (localNow.Month is 1)
            return new ObligationYearHandover(currentObligationYear, IncomingObligationYear: currentObligationYear + 1);

        var cutover = new DateTimeOffset(localNow.Year, 2, 1, 0, 0, 0, localNow.Offset);
        if (localNow >= cutover && localNow < cutover.Add(outgoingYearGracePeriod))
        {
            return new ObligationYearHandover(
                currentObligationYear,
                OutgoingObligationYear: currentObligationYear - 1,
                OutgoingYearCutoverAt: cutover.UtcDateTime
            );
        }

        return new ObligationYearHandover(currentObligationYear);
    }

    private static int GetCurrentObligationYear(DateTimeOffset localNow) =>
        localNow.Month is 1 ? localNow.Year - 1 : localNow.Year;
}
