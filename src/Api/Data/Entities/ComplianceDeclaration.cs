using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

// ReSharper disable MemberCanBePrivate.Global

namespace Defra.WasteObligations.Api.Data.Entities;

[BsonIgnoreExtraElements]
public record ComplianceDeclaration
{
    public const string SchemaVersionValue = "v1.2";

    public ObjectId Id { get; init; }
    public string SchemaVersion { get; private init; } = SchemaVersionValue;
    public int Version { get; init; } = 1;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime Created { get; init; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime Updated { get; init; }
    public ComplianceDeclarationStatus Status { get; init; }
    public required Organisation Organisation { get; init; }
    public int ObligationYear { get; init; }
    public IEnumerable<Obligation> Obligations { get; init; } = [];
    public required string ObligationStatus { get; init; }
    public required string SubmitterName { get; init; }
    public IEnumerable<AuditEntry> Audit { get; init; } = [];
    public bool IsRegulation43Compliant { get; init; }
    public decimal? ObligationCoveragePercentage { get; init; }

    public ComplianceDeclaration Submit(User user, DateTime timestamp)
    {
        GuardTimestamp(timestamp);

        return this with
        {
            Id = ObjectId.GenerateNewId(),
            Status = ComplianceDeclarationStatus.Submitted,
            ObligationCoveragePercentage = ObligationCoveragePercentageCalculator.CalculateFromObligations(Obligations),
            Audit = new List<AuditEntry>
            {
                new(nameof(ComplianceDeclarationStatus.Submitted)) { User = user, Timestamp = timestamp },
            },
        };
    }

    public ComplianceDeclaration UpdateStatus(
        ComplianceDeclarationStatus newStatus,
        string? reason,
        User user,
        DateTime timestamp
    )
    {
        GuardTimestamp(timestamp);

        if (!CanTransition(Status, newStatus))
            throw new EntityException($"Invalid status transition from {Status} to {newStatus}");

        return this with
        {
            Status = newStatus,
            Audit =
            [
                .. Audit,
                reason is not null
                    ? new ReasonAuditEntry(newStatus.ToString())
                    {
                        User = user,
                        Timestamp = timestamp,
                        Reason = reason,
                    }
                    : new AuditEntry(newStatus.ToString()) { User = user, Timestamp = timestamp },
            ],
        };
    }

    private static void GuardTimestamp(DateTime timestamp)
    {
        if (timestamp.Kind is not DateTimeKind.Utc)
            throw new ArgumentException("Timestamp should be UTC");
    }

    private static bool CanTransition(ComplianceDeclarationStatus current, ComplianceDeclarationStatus next) =>
        current switch
        {
            ComplianceDeclarationStatus.Submitted => next
                is ComplianceDeclarationStatus.Accepted
                    or ComplianceDeclarationStatus.Cancelled,
            ComplianceDeclarationStatus.Accepted => next is ComplianceDeclarationStatus.Cancelled,
            _ => false,
        };
}
