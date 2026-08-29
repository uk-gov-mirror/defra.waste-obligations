namespace Defra.WasteObligations.Api.IntegrationTests.Infrastructure;

public record MongoQueryProfileReport(
    IReadOnlyCollection<MongoQueryProfile> Queries,
    IReadOnlyCollection<MongoIndexUsage> UnusedSecondaryIndexes
)
{
    public IReadOnlyCollection<MongoQueryProfile> QueriesWithoutAnIndex => Queries.Where(x => !x.UsesIndex).ToArray();
}
