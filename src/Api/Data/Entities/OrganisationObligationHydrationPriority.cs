namespace Defra.WasteObligations.Api.Data.Entities;

public enum OrganisationObligationHydrationPriority
{
    NewEligible,
    ScheduledRefresh,
    Retry,
    Reconciliation,
}
