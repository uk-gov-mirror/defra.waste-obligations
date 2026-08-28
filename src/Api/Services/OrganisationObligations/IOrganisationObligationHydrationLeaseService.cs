namespace Defra.WasteObligations.Api.Services.OrganisationObligations;

public interface IOrganisationObligationHydrationLeaseService
{
    Task<bool> TryAcquire(TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> TryRenew(TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task Release(CancellationToken cancellationToken);
}
