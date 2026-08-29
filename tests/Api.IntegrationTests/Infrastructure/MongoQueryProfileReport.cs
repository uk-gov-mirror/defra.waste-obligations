using AwesomeAssertions;

namespace Defra.WasteObligations.Api.IntegrationTests.Infrastructure;

public record MongoQueryProfileReport(
    IReadOnlyCollection<MongoQueryProfile> Queries,
    IReadOnlyCollection<MongoIndexUsage> UnusedSecondaryIndexes
)
{
    public IReadOnlyCollection<MongoQueryProfile> QueriesWithoutAnIndex => Queries.Where(x => !x.UsesIndex).ToArray();

    public void AssertIndexesUsed(IReadOnlyCollection<MongoQueryProfileAllowance>? allowances = null)
    {
        var acceptedAllowances = allowances ?? [];
        var unexpectedQueries = QueriesWithoutAnIndex
            .Where(query => !acceptedAllowances.Any(allowance => allowance.Allows(query)))
            .ToArray();
        unexpectedQueries
            .Should()
            .BeEmpty(
                "each direct integration-test MongoDB query must use an index or have a narrowly matched accepted allowance. Unindexed queries: {0}",
                string.Join(
                    "; ",
                    unexpectedQueries.Select(x => $"{x.Operation} {x.Namespace} {x.FilterShape} ({x.PlanSummary})")
                )
            );
        var unusedAllowances = acceptedAllowances
            .Where(allowance => !QueriesWithoutAnIndex.Any(query => allowance.Allows(query)))
            .ToArray();
        unusedAllowances.Should().BeEmpty("each MongoDB query allowance must match a current unindexed query");
    }
}
