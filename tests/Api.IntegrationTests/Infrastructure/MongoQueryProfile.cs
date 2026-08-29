using MongoDB.Bson;

namespace Defra.WasteObligations.Api.IntegrationTests.Infrastructure;

public record MongoQueryProfile(
    string Operation,
    string Namespace,
    BsonDocument Command,
    string PlanSummary,
    long KeysExamined,
    long DocumentsExamined,
    IReadOnlyCollection<string> IndexNames
)
{
    public bool UsesIndex =>
        IndexNames.Count > 0
        || PlanSummary.Contains("IXSCAN", StringComparison.Ordinal)
        || PlanSummary.Contains("IDHACK", StringComparison.Ordinal)
        || PlanSummary.Contains("COUNT_SCAN", StringComparison.Ordinal)
        || PlanSummary.Contains("DISTINCT_SCAN", StringComparison.Ordinal);
}
