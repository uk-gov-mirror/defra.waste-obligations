namespace Defra.WasteObligations.Api.Data;

public record UnsubmittedOrganisationSort
{
    public UnsubmittedOrganisationSortField Field { get; init; }
    public UnsubmittedOrganisationSortDirection Direction { get; init; }
}
