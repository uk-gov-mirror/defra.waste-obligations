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

    [JsonPropertyName("organisationName")]
    public required string OrganisationName { get; init; }

    [JsonPropertyName("organisationReferenceNumber")]
    public required string OrganisationReferenceNumber { get; init; }

    [JsonPropertyName("recyclingObligationsMet")]
    public bool? RecyclingObligationsMet { get; init; }

    [JsonPropertyName("obligationCoveragePercentage")]
    public decimal ObligationCoveragePercentage { get; init; }
}
