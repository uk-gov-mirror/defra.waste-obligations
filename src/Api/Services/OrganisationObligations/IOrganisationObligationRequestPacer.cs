namespace Defra.WasteObligations.Api.Services.OrganisationObligations;

public interface IOrganisationObligationRequestPacer
{
    Task Wait(CancellationToken cancellationToken);
}
