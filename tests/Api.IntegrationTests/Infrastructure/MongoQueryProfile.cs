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
    public string FilterShape => Shape(Filter(Command));

    public bool UsesIndex =>
        PlanSummary == "EOF"
        || IndexNames.Count > 0
        || PlanSummary.Contains("IXSCAN", StringComparison.Ordinal)
        || PlanSummary.Contains("IDHACK", StringComparison.Ordinal)
        || PlanSummary.Contains("COUNT_SCAN", StringComparison.Ordinal)
        || PlanSummary.Contains("DISTINCT_SCAN", StringComparison.Ordinal);

    private static string Shape(BsonValue value) =>
        value switch
        {
            BsonDocument document =>
                $"{{{string.Join(",", document.Elements.OrderBy(x => x.Name).Select(x => $"{x.Name}:{Shape(x.Value)}"))}}}",
            BsonArray array => $"[{string.Join(",", array.Select(Shape).Distinct().Order())}]",
            _ => "?",
        };

    private static BsonValue Filter(BsonDocument command)
    {
        if (command.TryGetValue("filter", out var filter))
            return filter;

        if (command.TryGetValue("q", out var query))
            return query;

        if (command.TryGetValue("pipeline", out var pipeline) && pipeline is BsonArray stages)
        {
            var match = stages.OfType<BsonDocument>().FirstOrDefault(x => x.TryGetValue("$match", out _));

            if (match is not null)
                return match["$match"];
        }

        return new BsonDocument();
    }
}
