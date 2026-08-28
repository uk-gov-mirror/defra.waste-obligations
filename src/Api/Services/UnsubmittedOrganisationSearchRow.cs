using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Defra.WasteObligations.Api.Services;

[BsonIgnoreExtraElements]
public record UnsubmittedOrganisationSearchRow
{
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid OrganisationId { get; init; }

    public int ObligationYear { get; init; }
    public RegistrationType RegistrationType { get; init; }
    public required string Name { get; init; }
    public required string ReferenceNumber { get; init; }
    public bool? RecyclingObligationsMet { get; init; }
    public decimal ObligationCoveragePercentage { get; init; }
}
