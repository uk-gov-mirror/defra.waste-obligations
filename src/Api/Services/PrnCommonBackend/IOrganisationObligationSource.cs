namespace Defra.WasteObligations.Api.Services.PrnCommonBackend;

public interface IOrganisationObligationSource
{
    Task<IEnumerable<Obligation>> ReadObligations(Guid organisationId, int year, CancellationToken cancellationToken);
}
