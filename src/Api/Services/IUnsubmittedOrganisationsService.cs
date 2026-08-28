using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;

namespace Defra.WasteObligations.Api.Services;

public interface IUnsubmittedOrganisationsService
{
    Task<UnsubmittedOrganisationSearchResult> Search(
        int? obligationYear,
        IReadOnlyCollection<RegistrationType>? registrationTypes,
        string? search,
        IReadOnlyCollection<UnsubmittedOrganisationSort>? sort,
        int page,
        int pageSize,
        CancellationToken cancellationToken
    );
}
