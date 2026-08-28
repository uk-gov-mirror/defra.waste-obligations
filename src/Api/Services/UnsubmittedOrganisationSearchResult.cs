namespace Defra.WasteObligations.Api.Services;

public record UnsubmittedOrganisationSearchResult
{
    public required IReadOnlyList<UnsubmittedOrganisationSearchRow> Rows { get; init; }
    public required int Total { get; init; }
}
