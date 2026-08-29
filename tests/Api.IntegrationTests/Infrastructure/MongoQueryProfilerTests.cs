using System.Net;
using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.Testing.Fixtures.Entities;

namespace Defra.WasteObligations.Api.IntegrationTests.Infrastructure;

public class MongoQueryProfilerTests : IntegrationTestBase
{
    [Fact]
    public async Task Profile_WhenAnApiQueryUsesAnIndex_ShouldRecordTheQueryPlan()
    {
        var declarations = Enumerable
            .Range(0, 100)
            .Select(_ => ComplianceDeclarationFixture.Default().Create())
            .ToArray();
        await ComplianceDeclarations.InsertManyAsync(
            declarations,
            cancellationToken: TestContext.Current.CancellationToken
        );

        await using var profiler = await MongoQueryProfiler.Start(
            GetMongoDatabase(),
            [MongoQueryProfiler.ApiApplicationName],
            TestContext.Current.CancellationToken
        );
        using var client = CreateClient();
        var response = await client.GetAsync(
            "/compliance-declarations?obligationYear=2026&status=Submitted",
            TestContext.Current.CancellationToken
        );
        var profile = await profiler.Stop(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        profile.Queries.Should().Contain(x => x.Namespace == "waste-obligations.ComplianceDeclaration");
        profile.QueriesWithoutAnIndex.Should().BeEmpty();
        profile
            .Queries.Should()
            .Contain(x =>
                x.PlanSummary == "COUNT_SCAN { obligationYear: 1, status: 1, organisation.registrationType: 1 }"
            );
    }
}
