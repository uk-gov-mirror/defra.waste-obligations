using System.Text.Json.Serialization;

namespace Defra.WasteObligations.Api.Dtos;

public record UnsubmittedOrganisationsPaged
{
    [JsonPropertyName("unsubmittedOrganisations")]
    public IEnumerable<UnsubmittedOrganisation> UnsubmittedOrganisations { get; init; } = [];

    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; }
}
