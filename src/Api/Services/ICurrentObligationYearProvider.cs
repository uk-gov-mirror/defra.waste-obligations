namespace Defra.WasteObligations.Api.Services;

public interface ICurrentObligationYearProvider
{
    int GetCurrentObligationYear();
    ObligationYearHandover GetHandover(TimeSpan outgoingYearGracePeriod);
}
