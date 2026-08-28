using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Services;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Defra.WasteObligations.Api.Services.OrganisationObligations;

public class OrganisationObligationHydrationLeaseService(
    IMongoDatabase database,
    TimeProvider timeProvider,
    ILogger<OrganisationObligationHydrationLeaseService> logger
) : IOrganisationObligationHydrationLeaseService
{
    private readonly BackgroundWorkerLeaseService _leaseService = new(
        database,
        timeProvider,
        logger,
        BackgroundWorkerLease.CollectionName,
        BackgroundWorkerLease.OrganisationObligationHydrationLeaseId,
        "organisation obligation hydration"
    );

    public Task<bool> TryAcquire(TimeSpan leaseDuration, CancellationToken cancellationToken) =>
        _leaseService.TryAcquire(leaseDuration, cancellationToken);

    public Task<bool> TryRenew(TimeSpan leaseDuration, CancellationToken cancellationToken) =>
        _leaseService.TryRenew(leaseDuration, cancellationToken);

    public Task Release(CancellationToken cancellationToken) => _leaseService.Release(cancellationToken);
}
