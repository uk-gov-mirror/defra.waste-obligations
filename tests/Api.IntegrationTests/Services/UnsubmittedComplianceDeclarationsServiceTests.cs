using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Services;
using Defra.WasteObligations.Api.Services.OrganisationEligibility;
using Defra.WasteObligations.Testing;
using Defra.WasteObligations.Testing.Fixtures.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using OrganisationComplianceDeclarationEligibilityEntity = Defra.WasteObligations.Api.Data.Entities.OrganisationComplianceDeclarationEligibility;

namespace Defra.WasteObligations.Api.IntegrationTests.Services;

public class UnsubmittedOrganisationsServiceTests : IntegrationTestBase
{
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Search_WhenReady_ShouldReturnOnlyEligibleRowsAndApplySortingAndPaging()
    {
        const string generation = "generation";
        var alpha = Guid.NewGuid();
        var beta = Guid.NewGuid();
        var submitted = Guid.NewGuid();
        await SetReadySnapshot(generation);
        await OrganisationComplianceDeclarationEligibilities.InsertManyAsync(
            [
                Eligibility(alpha, generation, "Alpha Packaging", "100001"),
                Eligibility(beta, generation, "Beta Packaging", "100002"),
                Eligibility(submitted, generation, "Submitted Packaging", "100003") with
                {
                    IsVisibleInUnsubmittedView = false,
                },
                Eligibility(Guid.NewGuid(), generation, "Cancelled Packaging", "100004") with
                {
                    RegistrationStatus = OrganisationRegistrationStatus.Cancelled,
                    IsVisibleInUnsubmittedView = false,
                },
                Eligibility(Guid.NewGuid(), generation, "Unresolved Packaging", null) with
                {
                    ReferenceNumberResolutionState = OrganisationReferenceNumberResolutionState.Pending,
                    IsVisibleInUnsubmittedView = false,
                },
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );
        var subject = CreateSubject();

        var descending = await subject.Search(
            2026,
            [RegistrationType.DirectProducer],
            null,
            [
                new UnsubmittedOrganisationSort
                {
                    Field = UnsubmittedOrganisationSortField.Name,
                    Direction = UnsubmittedOrganisationSortDirection.Descending,
                },
            ],
            page: 1,
            pageSize: 1,
            TestContext.Current.CancellationToken
        );
        var ascendingSecondPage = await subject.Search(
            2026,
            [RegistrationType.DirectProducer],
            null,
            null,
            page: 2,
            pageSize: 1,
            TestContext.Current.CancellationToken
        );

        descending.Total.Should().Be(2);
        descending.Rows.Should().ContainSingle().Which.OrganisationId.Should().Be(beta);
        descending.Rows.Single().ReferenceNumber.Should().Be("100002");
        ascendingSecondPage.Total.Should().Be(2);
        ascendingSecondPage.Rows.Should().ContainSingle().Which.OrganisationId.Should().Be(beta);
    }

    [Fact]
    public async Task Search_WhenNoEligibleRows_ShouldReturnAnEmptyPage()
    {
        await SetReadySnapshot("generation");
        var subject = CreateSubject();

        var result = await subject.Search(
            2026,
            [RegistrationType.DirectProducer],
            null,
            null,
            page: 1,
            pageSize: 20,
            TestContext.Current.CancellationToken
        );

        result.Rows.Should().BeEmpty();
        result.Total.Should().Be(0);
    }

    [Fact]
    public async Task Search_WhenYearAndRegistrationTypeAreOptional_ShouldApplyOnlyTheProvidedFilters()
    {
        const string generation = "generation";
        var directCurrentYear = Guid.NewGuid();
        var schemeCurrentYear = Guid.NewGuid();
        var directHistoricYear = Guid.NewGuid();
        await SetReadySnapshot(generation);
        await OrganisationComplianceDeclarationEligibilities.InsertManyAsync(
            [
                Eligibility(directCurrentYear, generation, "Alpha Packaging", "100001"),
                Eligibility(schemeCurrentYear, generation, "Bravo Scheme", "200001") with
                {
                    RegistrationType = RegistrationType.ComplianceScheme,
                },
                Eligibility(directHistoricYear, generation, "Charlie Packaging", "100002") with
                {
                    ObligationYear = 2025,
                },
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );
        var subject = CreateSubject();

        var allRows = await subject.Search(
            null,
            [],
            null,
            null,
            page: 1,
            pageSize: 20,
            TestContext.Current.CancellationToken
        );
        var currentYearRows = await subject.Search(
            2026,
            [],
            null,
            null,
            page: 1,
            pageSize: 20,
            TestContext.Current.CancellationToken
        );
        var schemeRows = await subject.Search(
            null,
            [RegistrationType.ComplianceScheme],
            null,
            null,
            page: 1,
            pageSize: 20,
            TestContext.Current.CancellationToken
        );
        var bothRegistrationTypes = await subject.Search(
            null,
            [RegistrationType.DirectProducer, RegistrationType.ComplianceScheme],
            null,
            null,
            page: 1,
            pageSize: 20,
            TestContext.Current.CancellationToken
        );

        allRows
            .Rows.Select(x => x.OrganisationId)
            .Should()
            .Equal(directCurrentYear, schemeCurrentYear, directHistoricYear);
        currentYearRows.Rows.Select(x => x.OrganisationId).Should().Equal(directCurrentYear, schemeCurrentYear);
        schemeRows.Rows.Select(x => x.OrganisationId).Should().ContainSingle().Which.Should().Be(schemeCurrentYear);
        bothRegistrationTypes
            .Rows.Select(x => x.OrganisationId)
            .Should()
            .Equal(directCurrentYear, schemeCurrentYear, directHistoricYear);
    }

    [Fact]
    public async Task Search_WhenSearchMatchesNameOrReference_ShouldReturnCaseInsensitivePartialMatches()
    {
        const string generation = "generation";
        var nameMatchOrganisationId = Guid.NewGuid();
        var referenceMatchOrganisationId = Guid.NewGuid();
        await SetReadySnapshot(generation);
        await OrganisationComplianceDeclarationEligibilities.InsertManyAsync(
            [
                Eligibility(nameMatchOrganisationId, generation, "Alpha Packaging", "100001"),
                Eligibility(Guid.NewGuid(), generation, "Bravo Scheme", "100002", tradingName: "Northern Operator"),
                Eligibility(referenceMatchOrganisationId, generation, "Charlie Recycling", "100003"),
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );
        var subject = CreateSubject();

        var nameResult = await subject.Search(
            2026,
            [RegistrationType.DirectProducer],
            "PHA PAC",
            null,
            page: 1,
            pageSize: 20,
            TestContext.Current.CancellationToken
        );
        var referenceResult = await subject.Search(
            2026,
            [RegistrationType.DirectProducer],
            "0003",
            null,
            page: 1,
            pageSize: 20,
            TestContext.Current.CancellationToken
        );

        nameResult.Total.Should().Be(1);
        nameResult.Rows.Should().ContainSingle().Which.OrganisationId.Should().Be(nameMatchOrganisationId);
        referenceResult.Total.Should().Be(1);
        referenceResult.Rows.Should().ContainSingle().Which.OrganisationId.Should().Be(referenceMatchOrganisationId);
    }

    [Fact]
    public async Task Search_WhenSearchMatchesOnlyTradingName_ShouldNotReturnTheOrganisation()
    {
        const string generation = "generation";
        await SetReadySnapshot(generation);
        await OrganisationComplianceDeclarationEligibilities.InsertOneAsync(
            Eligibility(Guid.NewGuid(), generation, "Bravo Scheme", "100002", tradingName: "Northern Operator"),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var subject = CreateSubject();

        var result = await subject.Search(
            2026,
            [RegistrationType.DirectProducer],
            "operator",
            null,
            page: 1,
            pageSize: 20,
            TestContext.Current.CancellationToken
        );

        result.Total.Should().Be(0);
        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_ShouldReturnMetricsStoredOnTheEligibilityRows()
    {
        const string generation = "generation";
        var readyOrganisationId = Guid.NewGuid();
        var failedOrganisationId = Guid.NewGuid();
        var staleOrganisationId = Guid.NewGuid();
        await SetReadySnapshot(generation);
        await OrganisationComplianceDeclarationEligibilities.InsertManyAsync(
            [
                Eligibility(readyOrganisationId, generation, "Ready Packaging", "100001") with
                {
                    RecyclingObligationsMet = true,
                    ObligationCoveragePercentage = 80,
                },
                Eligibility(failedOrganisationId, generation, "Failed Packaging", "100002") with
                {
                    RecyclingObligationsMet = false,
                    ObligationCoveragePercentage = 40,
                },
                Eligibility(staleOrganisationId, generation, "Stale Packaging", "100003") with
                {
                    RecyclingObligationsMet = true,
                    ObligationCoveragePercentage = 60,
                },
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );
        var subject = CreateSubject();

        var result = await subject.Search(
            2026,
            [RegistrationType.DirectProducer],
            null,
            null,
            page: 1,
            pageSize: 20,
            TestContext.Current.CancellationToken
        );

        result.Total.Should().Be(3);
        var rows = result.Rows.ToDictionary(x => x.OrganisationId);
        rows[readyOrganisationId].RecyclingObligationsMet.Should().BeTrue();
        rows[readyOrganisationId].ObligationCoveragePercentage.Should().Be(80);
        rows[failedOrganisationId].RecyclingObligationsMet.Should().BeFalse();
        rows[failedOrganisationId].ObligationCoveragePercentage.Should().Be(40);
        rows[staleOrganisationId].RecyclingObligationsMet.Should().BeTrue();
        rows[staleOrganisationId].ObligationCoveragePercentage.Should().Be(60);
    }

    [Fact]
    public async Task Search_WhenRowsHaveDifferentSortValues_ShouldUseTheRequestedIndexedOrder()
    {
        const string generation = "generation";
        var alpha = Guid.NewGuid();
        var bravo = Guid.NewGuid();
        var charlie = Guid.NewGuid();
        await SetReadySnapshot(generation);
        await OrganisationComplianceDeclarationEligibilities.InsertManyAsync(
            [
                Eligibility(alpha, generation, "Alpha Packaging", "100003") with
                {
                    RecyclingObligationsMet = true,
                    ObligationCoveragePercentage = 40,
                },
                Eligibility(bravo, generation, "Bravo Packaging", "100001") with
                {
                    RecyclingObligationsMet = false,
                    ObligationCoveragePercentage = 80,
                },
                Eligibility(charlie, generation, "Charlie Packaging", "100002") with
                {
                    RecyclingObligationsMet = true,
                    ObligationCoveragePercentage = 60,
                },
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );
        var subject = CreateSubject();

        var byReference = await subject.Search(
            2026,
            [RegistrationType.DirectProducer],
            null,
            [
                new UnsubmittedOrganisationSort
                {
                    Field = UnsubmittedOrganisationSortField.ReferenceNumber,
                    Direction = UnsubmittedOrganisationSortDirection.Ascending,
                },
            ],
            page: 1,
            pageSize: 3,
            TestContext.Current.CancellationToken
        );
        var byRecycling = await subject.Search(
            2026,
            [RegistrationType.DirectProducer],
            null,
            [
                new UnsubmittedOrganisationSort
                {
                    Field = UnsubmittedOrganisationSortField.RecyclingObligationsMet,
                    Direction = UnsubmittedOrganisationSortDirection.Ascending,
                },
            ],
            page: 1,
            pageSize: 3,
            TestContext.Current.CancellationToken
        );
        var byPercentage = await subject.Search(
            2026,
            [RegistrationType.DirectProducer],
            null,
            [
                new UnsubmittedOrganisationSort
                {
                    Field = UnsubmittedOrganisationSortField.ObligationCoveragePercentage,
                    Direction = UnsubmittedOrganisationSortDirection.Descending,
                },
            ],
            page: 1,
            pageSize: 3,
            TestContext.Current.CancellationToken
        );

        byReference.Rows.Select(x => x.OrganisationId).Should().Equal(bravo, charlie, alpha);
        byRecycling.Rows.Select(x => x.OrganisationId).Should().Equal(bravo, alpha, charlie);
        byPercentage.Rows.Select(x => x.OrganisationId).Should().Equal(bravo, charlie, alpha);
    }

    [Fact]
    public async Task Search_WhenMultipleSortFieldsAreRequested_ShouldApplyThemInPriorityOrder()
    {
        const string generation = "generation";
        var alpha = Guid.NewGuid();
        var bravo = Guid.NewGuid();
        var charlie = Guid.NewGuid();
        await SetReadySnapshot(generation);
        await OrganisationComplianceDeclarationEligibilities.InsertManyAsync(
            [
                Eligibility(alpha, generation, "Alpha Packaging", "100003") with
                {
                    ObligationCoveragePercentage = 80,
                },
                Eligibility(bravo, generation, "Bravo Packaging", "100001") with
                {
                    ObligationCoveragePercentage = 80,
                },
                Eligibility(charlie, generation, "Charlie Packaging", "100002") with
                {
                    ObligationCoveragePercentage = 60,
                },
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );
        var subject = CreateSubject();

        var result = await subject.Search(
            2026,
            [RegistrationType.DirectProducer],
            null,
            [
                new UnsubmittedOrganisationSort
                {
                    Field = UnsubmittedOrganisationSortField.ObligationCoveragePercentage,
                    Direction = UnsubmittedOrganisationSortDirection.Descending,
                },
                new UnsubmittedOrganisationSort
                {
                    Field = UnsubmittedOrganisationSortField.ReferenceNumber,
                    Direction = UnsubmittedOrganisationSortDirection.Ascending,
                },
            ],
            page: 1,
            pageSize: 20,
            TestContext.Current.CancellationToken
        );

        result.Rows.Select(x => x.OrganisationId).Should().Equal(bravo, alpha, charlie);
    }

    [Theory]
    [InlineData("referenceNumber", "Generation_IsVisibleInUnsubmittedView_ReferenceNumber_Name_OrganisationId")]
    [InlineData(
        "recyclingObligationsMet",
        "Generation_IsVisibleInUnsubmittedView_RecyclingObligationsMet_Name_OrganisationId"
    )]
    [InlineData(
        "obligationCoveragePercentage",
        "Generation_IsVisibleInUnsubmittedView_ObligationCoveragePercentage_Name_OrganisationId"
    )]
    public async Task SearchPlan_WhenUsingAnObligationSort_ShouldUseTheDedicatedIndex(
        string sortField,
        string indexName
    )
    {
        const string generation = "generation";
        await SetReadySnapshot(generation);
        await OrganisationComplianceDeclarationEligibilities.InsertOneAsync(
            Eligibility(Guid.NewGuid(), generation, "Alpha Packaging", "100001"),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var command = new BsonDocument
        {
            ["explain"] = new BsonDocument
            {
                ["find"] = nameof(OrganisationComplianceDeclarationEligibility),
                ["filter"] = new BsonDocument
                {
                    ["generation"] = generation,
                    ["obligationYear"] = 2026,
                    ["registrationType"] = (int)RegistrationType.DirectProducer,
                    ["isVisibleInUnsubmittedView"] = true,
                },
                ["sort"] = new BsonDocument
                {
                    [sortField] = 1,
                    ["name"] = 1,
                    ["organisationId"] = 1,
                },
            },
            ["verbosity"] = "queryPlanner",
        };

        var plan = await GetMongoDatabase()
            .RunCommandAsync<BsonDocument>(command, cancellationToken: TestContext.Current.CancellationToken);
        var renderedWinningPlan = plan["queryPlanner"]["winningPlan"].ToJson();

        renderedWinningPlan.Should().Contain(indexName);
        renderedWinningPlan.Should().NotContain("\"stage\" : \"SORT\"");
    }

    [Fact]
    public async Task SearchPlan_WhenScopeFiltersAreOmitted_ShouldUseTheDefaultSortIndex()
    {
        const string generation = "generation";
        const string indexName = "Generation_IsVisibleInUnsubmittedView_Name_OrganisationId";
        await SetReadySnapshot(generation);
        await OrganisationComplianceDeclarationEligibilities.InsertOneAsync(
            Eligibility(Guid.NewGuid(), generation, "Alpha Packaging", "100001"),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var command = new BsonDocument
        {
            ["explain"] = new BsonDocument
            {
                ["find"] = nameof(OrganisationComplianceDeclarationEligibility),
                ["filter"] = new BsonDocument { ["generation"] = generation, ["isVisibleInUnsubmittedView"] = true },
                ["sort"] = new BsonDocument { ["name"] = 1, ["organisationId"] = 1 },
            },
            ["verbosity"] = "queryPlanner",
        };

        var plan = await GetMongoDatabase()
            .RunCommandAsync<BsonDocument>(command, cancellationToken: TestContext.Current.CancellationToken);
        var renderedWinningPlan = plan["queryPlanner"]["winningPlan"].ToJson();

        renderedWinningPlan.Should().Contain(indexName);
        renderedWinningPlan.Should().NotContain("\"stage\" : \"SORT\"");
    }

    [Fact]
    public async Task Search_WhenNoActiveGeneration_ShouldReturnAnEmptyPageAndLogAnError()
    {
        var logger = new RecordingLogger<UnsubmittedOrganisationsService>();
        var subject = CreateSubject(logger);

        var result = await subject.Search(
            2026,
            [RegistrationType.DirectProducer],
            null,
            null,
            page: 1,
            pageSize: 20,
            TestContext.Current.CancellationToken
        );

        result.Rows.Should().BeEmpty();
        result.Total.Should().Be(0);
        logger
            .Entries.Should()
            .ContainSingle(x =>
                x.Level == LogLevel.Error
                && x.Message == "Unsubmitted organisation query has no active organisation generation"
            );
    }

    [Fact]
    public async Task Search_WhenActiveGenerationIsStale_ShouldReturnItsDataAndLogAnError()
    {
        const string generation = "stale-generation";
        var organisationId = Guid.NewGuid();
        var verifiedAt = _timeProvider.GetUtcNow().UtcDateTime.AddHours(-3);
        await OrganisationEligibilitySnapshots.InsertOneAsync(
            new OrganisationEligibilitySnapshot
            {
                Id = OrganisationEligibilitySnapshot.SnapshotId,
                ActiveGeneration = generation,
                ActiveContentFingerprint = "fingerprint",
                ActiveRowCount = 1,
                ActiveGenerationPromotedAt = verifiedAt,
                LastVerifiedAt = verifiedAt,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        await OrganisationComplianceDeclarationEligibilities.InsertOneAsync(
            Eligibility(organisationId, generation, "Alpha Packaging", "100001"),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var logger = new RecordingLogger<UnsubmittedOrganisationsService>();
        var subject = CreateSubject(logger);

        var result = await subject.Search(
            2026,
            [RegistrationType.DirectProducer],
            null,
            null,
            page: 1,
            pageSize: 20,
            TestContext.Current.CancellationToken
        );

        result.Total.Should().Be(1);
        result.Rows.Should().ContainSingle().Which.OrganisationId.Should().Be(organisationId);
        logger
            .Entries.Should()
            .ContainSingle(x =>
                x.Level == LogLevel.Error
                && x.Message.StartsWith(
                    "Unsubmitted organisation query is using an organisation generation last verified at"
                )
            );
    }

    private UnsubmittedOrganisationsService CreateSubject(ILogger<UnsubmittedOrganisationsService>? logger = null) =>
        new(
            new MongoDbContext(
                GetMongoDatabase(),
                Options.Create(new MongoDbOptions()),
                NullLogger<MongoDbContext>.Instance
            ),
            Options.Create(new OrganisationEligibilityOptions { MaximumAllowedStaleness = TimeSpan.FromHours(2) }),
            _timeProvider,
            logger ?? NullLogger<UnsubmittedOrganisationsService>.Instance
        );

    private async Task SetReadySnapshot(string generation)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await OrganisationEligibilitySnapshots.InsertOneAsync(
            new OrganisationEligibilitySnapshot
            {
                Id = OrganisationEligibilitySnapshot.SnapshotId,
                ActiveGeneration = generation,
                ActiveContentFingerprint = "fingerprint",
                ActiveRowCount = 1,
                ActiveGenerationPromotedAt = now,
                LastVerifiedAt = now,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
    }

    private static OrganisationComplianceDeclarationEligibilityEntity Eligibility(
        Guid organisationId,
        string generation,
        string name,
        string? referenceNumber,
        string? tradingName = null
    ) =>
        OrganisationComplianceDeclarationEligibilityFixture
            .Default(organisationId)
            .With(x => x.Generation, generation)
            .With(x => x.Name, name)
            .With(x => x.TradingName, tradingName)
            .With(x => x.ReferenceNumber, referenceNumber)
            .With(x => x.SourceFingerprint, name)
            .Create();
}
