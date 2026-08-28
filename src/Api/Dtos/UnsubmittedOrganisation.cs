using System.Text.Json.Serialization;

namespace Defra.WasteObligations.Api.Dtos;

public record UnsubmittedOrganisation
{
    [JsonPropertyName("organisationId")]
    public Guid OrganisationId { get; init; }

    [JsonPropertyName("obligationYear")]
    public int ObligationYear { get; init; }

    [JsonPropertyName("registrationType")]
    public RegistrationType RegistrationType { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("referenceNumber")]
    public required string ReferenceNumber { get; init; }

    [JsonPropertyName("recyclingObligationsMet")]
    public bool? RecyclingObligationsMet { get; init; }

    [JsonPropertyName("obligationCoveragePercentage")]
    public decimal ObligationCoveragePercentage { get; init; }
}
