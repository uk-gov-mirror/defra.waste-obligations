using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Defra.WasteObligations.Api.Data.Entities;

[BsonIgnoreExtraElements]
public record OrganisationObligationSummary
{
    public ObjectId Id { get; init; } = ObjectId.GenerateNewId();

    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid OrganisationId { get; init; }

    public int ObligationYear { get; init; }
    public int ObligationCount { get; init; }
    public int TotalAcceptedTonnage { get; init; }
    public int TotalObligatedTonnage { get; init; }
    public bool? RecyclingObligationsMet { get; init; }
    public decimal? ObligationCoveragePercentage { get; init; }
    public string? SourceFingerprint { get; init; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastSuccessfulReadAt { get; init; }

    public string? DailyCalculationRunId { get; init; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime LastAttemptedAt { get; init; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime NextRefreshAt { get; init; }

    public OrganisationObligationHydrationPriority Priority { get; init; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime RequestedAt { get; init; }

    public bool IsHydrationActive { get; init; }

    public OrganisationObligationRefreshState RefreshState { get; init; }
    public int AttemptCount { get; init; }
    public string? LastFailure { get; init; }
}
