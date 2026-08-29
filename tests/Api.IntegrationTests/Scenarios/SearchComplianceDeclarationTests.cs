using System.Net.Http.Json;
using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Dtos;
using Defra.WasteObligations.Api.Services;
using Defra.WasteObligations.Api.Utils.Logging;
using Defra.WasteObligations.Api.Utils.Metrics;
using Defra.WasteObligations.AuditEvents;
using Defra.WasteObligations.AuditEvents.Data;
using Defra.WasteObligations.Testing;
using Defra.WasteObligations.Testing.Fakes;
using Defra.WasteObligations.Testing.Fixtures.Entities;
using Microsoft.AspNetCore.HeaderPropagation;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Defra.WasteObligations.Api.IntegrationTests.Scenarios;

public class SearchComplianceDeclarationTests : IntegrationTestBase
{
    private ComplianceDeclarationService Subject { get; }

    public SearchComplianceDeclarationTests()
    {
        var database = GetMongoApplicationDatabase();
        var auditEventDbContext = new AuditEventDbContext(database);
        var dbContext = new MongoDbContext(
            database,
            Options.Create(new MongoDbOptions()),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<MongoDbContext>>()
        );
        var auditEventService = new AuditEventService(
            auditEventDbContext,
            TimeProvider.System,
            new FakeEventIdGenerator()
        );

        Subject = new(
            dbContext,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<ComplianceDeclarationService>>(),
            TimeProvider.System,
            auditEventService,
            Substitute.For<IComplianceDeclarationMetrics>(),
            new TraceIdReader(
                new HeaderPropagationValues(),
                Options.Create(new TraceHeader { Name = TraceHeaderName })
            ),
            new UnsubmittedEligibilityVisibilityService(dbContext)
        );
    }

    [Fact]
    public async Task Search_WhenPaginationIsUsed_ShouldReturnAllResults()
    {
        const int recordCount = 5;
        const int pageSize = 2;
        var seededIds = new List<string>();

        for (var i = 0; i < recordCount; i++)
        {
            var entity = await Subject.Create(
                ComplianceDeclarationFixture.Default().Create(),
                TestContext.Current.CancellationToken
            );
            seededIds.Add(entity.Id.ToString());
        }

        var client = CreateClient();
        var collectedDeclarations = new List<ComplianceDeclaration>();
        var currentPage = 1;
        int totalCount;

        do
        {
            var query = EndpointQuery
                .New.Where(EndpointFilter.Page(currentPage))
                .Where(EndpointFilter.PageSize(pageSize));

            var response = await client.GetFromJsonAsync<ComplianceDeclarationsPaged>(
                Testing.Endpoints.ComplianceDeclarations.Search(query),
                TestContext.Current.CancellationToken
            );

            if (response == null)
                break;

            totalCount = response.Total;
            collectedDeclarations.AddRange(response.ComplianceDeclarations);
            currentPage++;
        } while (collectedDeclarations.Count < totalCount);

        collectedDeclarations.Should().HaveCount(recordCount);
        collectedDeclarations.Select(x => x.Id).Should().BeEquivalentTo(seededIds);
        collectedDeclarations.Should().OnlyContain(x => x.ObligationCoveragePercentage == 40m);
    }

    [Theory]
    [InlineData("zeina")] // partial organisation name
    [InlineData("OPERATOR CO")] // scheme operator name in a different case
    [InlineData("100245")] // reference number
    public async Task Search_WhenFilteringByTerm_ShouldReturnOnlyMatchingDeclarations(string term)
    {
        var matching = await SeedDeclaration("ZEINA FOODS LIMITED", "Operator Co", "100245");
        await SeedDeclaration("Unrelated Holdings", "Other Operator", "999999");

        var response = await Search(term);

        response!.Total.Should().Be(1);
        response.ComplianceDeclarations.Should().ContainSingle(x => x.Id == matching);
    }

    [Fact]
    public async Task Search_WhenNothingMatchesTheTerm_ShouldReturnAnEmptyPage()
    {
        await SeedDeclaration("ZEINA FOODS LIMITED", "Operator Co", "100245");

        var response = await Search("zzzznomatchzzzz");

        response!.Total.Should().Be(0);
        response.ComplianceDeclarations.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_WhenTermExceedsTheMaximumLength_ShouldBeBadRequest()
    {
        var client = CreateClient();

        var response = await client.GetAsync(
            Testing.Endpoints.ComplianceDeclarations.Search(
                EndpointQuery.New.Where(EndpointFilter.Search(new string('a', 101)))
            ),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    private static async Task<ComplianceDeclarationsPaged?> Search(string term)
    {
        var client = CreateClient();

        return await client.GetFromJsonAsync<ComplianceDeclarationsPaged>(
            Testing.Endpoints.ComplianceDeclarations.Search(EndpointQuery.New.Where(EndpointFilter.Search(term))),
            TestContext.Current.CancellationToken
        );
    }

    private async Task<string> SeedDeclaration(string name, string schemeOperatorName, string referenceNumber)
    {
        var entity = await Subject.Create(
            ComplianceDeclarationFixture
                .Default()
                .With(
                    x => x.Organisation,
                    OrganisationFixture
                        .Organisation()
                        .With(y => y.Name, name)
                        .With(y => y.SchemeOperatorName, schemeOperatorName)
                        .With(y => y.ReferenceNumber, referenceNumber)
                        .Create()
                )
                .Create(),
            TestContext.Current.CancellationToken
        );

        return entity.Id.ToString();
    }
}
