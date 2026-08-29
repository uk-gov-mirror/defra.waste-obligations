using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.IntegrationTests.Infrastructure;
using Defra.WasteObligations.Api.Services.OrganisationObligations;
using Defra.WasteObligations.Api.Services.PrnCommonBackend;
using Defra.WasteObligations.Api.Utils.Metrics;
using Defra.WasteObligations.Testing.Fixtures.PrnCommonBackend;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using ObligationStatus = Defra.WasteObligations.Api.Dtos.ObligationStatus;
using PrnObligation = Defra.WasteObligations.Api.Services.PrnCommonBackend.Obligation;

namespace Defra.WasteObligations.Api.IntegrationTests.Services.OrganisationObligations;

public class OrganisationObligationHydrationServiceTests : IntegrationTestBase
{
    private const int ObligationYear = 2026;
    private const string HydrationDueWorkIndexName = "ObligationYear_IsHydrationActive_Priority_NextRefreshAt";
    private const string HydrationEligibilityIndexName =
        "Generation_ObligationYear_RegistrationStatus_ReferenceNumberResolutionState_OrganisationId";
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
    private IOrganisationObligationSource ObligationSource { get; } = Substitute.For<IOrganisationObligationSource>();
    private IOrganisationObligationHydrationMetrics HydrationMetrics { get; } =
        Substitute.For<IOrganisationObligationHydrationMetrics>();

    [Fact]
    public async Task EnqueueNewEligible_WhenNoActiveGenerationExists_ShouldNotCreateSummary()
    {
        var subject = CreateSubject();

        var enqueuedCount = await subject.EnqueueNewEligible(ObligationYear, TestContext.Current.CancellationToken);

        enqueuedCount.Should().Be(0);
    }

    [Fact]
    public async Task EnqueueNewEligible_ShouldDeduplicateActiveRegisteredResolvedRows()
    {
        var organisationId = Guid.NewGuid();
        await InsertActiveSnapshot();
        await InsertEligibility(organisationId, RegistrationType.DirectProducer);
        await InsertEligibility(organisationId, RegistrationType.ComplianceScheme);
        await InsertEligibility(
            Guid.NewGuid(),
            RegistrationType.DirectProducer,
            registrationStatus: OrganisationRegistrationStatus.Cancelled
        );
        await InsertEligibility(
            Guid.NewGuid(),
            RegistrationType.DirectProducer,
            referenceResolutionState: OrganisationReferenceNumberResolutionState.NotFound
        );
        await InsertEligibility(Guid.NewGuid(), RegistrationType.DirectProducer, obligationYear: ObligationYear - 1);
        var subject = CreateSubject();

        var enqueuedCount = await subject.EnqueueNewEligible(ObligationYear, TestContext.Current.CancellationToken);

        enqueuedCount.Should().Be(1);
        var summary = await OrganisationObligationSummaries
            .Find(Builders<OrganisationObligationSummary>.Filter.Empty)
            .SingleAsync(TestContext.Current.CancellationToken);
        summary.OrganisationId.Should().Be(organisationId);
        summary.ObligationYear.Should().Be(ObligationYear);
        summary.Priority.Should().Be(OrganisationObligationHydrationPriority.NewEligible);
        summary.NextRefreshAt.Should().Be(_timeProvider.GetUtcNow().UtcDateTime);
        summary.RefreshState.Should().Be(OrganisationObligationRefreshState.Pending);
        summary.IsHydrationActive.Should().BeTrue();
    }

    [Fact]
    public async Task EnqueueNewEligible_WhenProfiled_ShouldUseTheHydrationEligibilityIndex()
    {
        var organisationId = Guid.NewGuid();
        await InsertActiveSnapshot();
        await InsertEligibility(organisationId, RegistrationType.DirectProducer);
        foreach (var _ in Enumerable.Range(0, 100))
        {
            await InsertEligibility(
                Guid.NewGuid(),
                RegistrationType.DirectProducer,
                obligationYear: ObligationYear - 1
            );
        }

        var subject = CreateSubject();
        await using var profiler = await MongoQueryProfiler.Start(
            GetMongoDatabase(),
            [MongoQueryProfiler.IntegrationTestApplicationName],
            TestContext.Current.CancellationToken
        );
        var enqueuedCount = await subject.EnqueueNewEligible(ObligationYear, TestContext.Current.CancellationToken);
        var profile = await profiler.Stop(TestContext.Current.CancellationToken);

        enqueuedCount.Should().Be(1);
        profile.QueriesWithoutAnIndex.Should().BeEmpty();
        profile
            .Queries.Should()
            .Contain(x =>
                x.Namespace == "waste-obligations.OrganisationComplianceDeclarationEligibility"
                && x.IndexNames.Contains(HydrationEligibilityIndexName)
            );
    }

    [Fact]
    public async Task EnqueueNewEligible_WhenASuccessfulSummaryExists_ShouldActivateItsExistingSummary()
    {
        var organisationId = Guid.NewGuid();
        await InsertActiveSnapshot();
        await InsertEligibility(organisationId, RegistrationType.DirectProducer);
        await InsertSummary(
            organisationId,
            OrganisationObligationRefreshState.Ready,
            _timeProvider.GetUtcNow().UtcDateTime
        );
        var subject = CreateSubject();

        var enqueuedCount = await subject.EnqueueNewEligible(ObligationYear, TestContext.Current.CancellationToken);

        enqueuedCount.Should().Be(0);
        var summary = await OrganisationObligationSummaries
            .Find(Builders<OrganisationObligationSummary>.Filter.Empty)
            .SingleAsync(TestContext.Current.CancellationToken);
        summary.IsHydrationActive.Should().BeTrue();
    }

    [Theory]
    [InlineData(OrganisationRegistrationStatus.Cancelled, OrganisationReferenceNumberResolutionState.Resolved)]
    [InlineData(OrganisationRegistrationStatus.Registered, OrganisationReferenceNumberResolutionState.NotFound)]
    public async Task HydrateDue_WhenQueuedOrganisationIsNoLongerEligible_ShouldDeactivateSummaryWithoutCallingSource(
        OrganisationRegistrationStatus registrationStatus,
        OrganisationReferenceNumberResolutionState referenceResolutionState
    )
    {
        var organisationId = Guid.NewGuid();
        await InsertEligibility(organisationId, RegistrationType.DirectProducer, generation: "previous");
        await InsertHydrationSummary(organisationId);
        await InsertEligibility(
            organisationId,
            RegistrationType.DirectProducer,
            registrationStatus: registrationStatus,
            referenceResolutionState: referenceResolutionState,
            generation: "current"
        );
        await InsertActiveSnapshot("current");
        var subject = CreateSubject();

        var processedCount = await subject.HydrateDue(ObligationYear, TestContext.Current.CancellationToken);

        processedCount.Should().Be(0);
        await ObligationSource
            .DidNotReceive()
            .ReadObligations(organisationId, ObligationYear, Arg.Any<CancellationToken>());
        var summary = await OrganisationObligationSummaries
            .Find(x => x.OrganisationId == organisationId && x.ObligationYear == ObligationYear)
            .SingleAsync(TestContext.Current.CancellationToken);
        summary.IsHydrationActive.Should().BeFalse();
    }

    [Fact]
    public async Task EnqueueReconciliation_ShouldMakePreCutoverScheduledWorkDue()
    {
        var organisationId = Guid.NewGuid();
        var readBeforeCutover = _timeProvider.GetUtcNow().AddMinutes(-2).UtcDateTime;
        var cutover = _timeProvider.GetUtcNow().AddMinutes(-1).UtcDateTime;
        var readAfterCutoverOrganisationId = Guid.NewGuid();
        await InsertActiveSnapshot();
        await InsertEligibility(organisationId, RegistrationType.DirectProducer);
        await InsertEligibility(readAfterCutoverOrganisationId, RegistrationType.DirectProducer);
        await InsertHydrationSummary(
            organisationId,
            nextRefreshAt: _timeProvider.GetUtcNow().AddHours(1).UtcDateTime,
            lastSuccessfulReadAt: readBeforeCutover
        );
        await InsertHydrationSummary(
            readAfterCutoverOrganisationId,
            nextRefreshAt: _timeProvider.GetUtcNow().AddHours(1).UtcDateTime,
            lastSuccessfulReadAt: _timeProvider.GetUtcNow().UtcDateTime
        );
        var subject = CreateSubject();

        var enqueuedCount = await subject.EnqueueReconciliation(
            ObligationYear,
            cutover,
            TestContext.Current.CancellationToken
        );

        enqueuedCount.Should().Be(1);
        var reconciledSummary = await OrganisationObligationSummaries
            .Find(x => x.OrganisationId == organisationId)
            .SingleAsync(TestContext.Current.CancellationToken);
        reconciledSummary.Priority.Should().Be(OrganisationObligationHydrationPriority.Reconciliation);
        reconciledSummary.NextRefreshAt.Should().Be(_timeProvider.GetUtcNow().UtcDateTime);
        var recentSummary = await OrganisationObligationSummaries
            .Find(x => x.OrganisationId == readAfterCutoverOrganisationId)
            .SingleAsync(TestContext.Current.CancellationToken);
        recentSummary.Priority.Should().Be(OrganisationObligationHydrationPriority.ScheduledRefresh);
        recentSummary.NextRefreshAt.Should().Be(_timeProvider.GetUtcNow().AddHours(1).UtcDateTime);
    }

    [Fact]
    public async Task HydrateDue_WhenSourceSucceeds_ShouldPersistReadySummaryAndScheduleRefresh()
    {
        var organisationId = Guid.NewGuid();
        await InsertActiveSnapshot();
        await InsertEligibility(organisationId, RegistrationType.DirectProducer);
        await InsertEligibility(organisationId, RegistrationType.DirectProducer, generation: "retained");
        ObligationSource
            .ReadObligations(organisationId, ObligationYear, Arg.Any<CancellationToken>())
            .Returns([
                CreateObligation("Glass", accepted: 15, obligated: 20, ObligationStatus.Met),
                CreateObligation("Plastic", accepted: 20, obligated: 20, ObligationStatus.NotMet),
            ]);
        var subject = CreateSubject();

        var processedCount = await subject.HydrateDue(ObligationYear, TestContext.Current.CancellationToken);

        processedCount.Should().Be(1);
        var summary = await OrganisationObligationSummaries
            .Find(x => x.OrganisationId == organisationId && x.ObligationYear == ObligationYear)
            .SingleAsync(TestContext.Current.CancellationToken);
        summary.ObligationCount.Should().Be(2);
        summary.TotalAcceptedTonnage.Should().Be(35);
        summary.TotalObligatedTonnage.Should().Be(40);
        summary.RecyclingObligationsMet.Should().BeFalse();
        summary.ObligationCoveragePercentage.Should().Be(88);
        summary.RefreshState.Should().Be(OrganisationObligationRefreshState.Ready);
        summary.LastSuccessfulReadAt.Should().Be(_timeProvider.GetUtcNow().UtcDateTime);
        summary.NextRefreshAt.Should().BeAfter(_timeProvider.GetUtcNow().UtcDateTime);
        summary.Priority.Should().Be(OrganisationObligationHydrationPriority.ScheduledRefresh);
        summary.IsHydrationActive.Should().BeTrue();
        var eligibility = await OrganisationComplianceDeclarationEligibilities
            .Find(x =>
                x.Generation == "active" && x.OrganisationId == organisationId && x.ObligationYear == ObligationYear
            )
            .SingleAsync(TestContext.Current.CancellationToken);
        eligibility.RecyclingObligationsMet.Should().BeFalse();
        eligibility.ObligationCoveragePercentage.Should().Be(88);
        var retainedEligibility = await OrganisationComplianceDeclarationEligibilities
            .Find(x =>
                x.Generation == "retained" && x.OrganisationId == organisationId && x.ObligationYear == ObligationYear
            )
            .SingleAsync(TestContext.Current.CancellationToken);
        retainedEligibility.RecyclingObligationsMet.Should().BeNull();
        retainedEligibility.ObligationCoveragePercentage.Should().Be(0);
        var snapshot = await OrganisationEligibilitySnapshots
            .Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleAsync(TestContext.Current.CancellationToken);

        snapshot.MaterialisedStateVersion.Should().Be(1);
        HydrationMetrics.Received(1).Succeeded();
        await ObligationSource
            .Received(1)
            .ReadObligations(organisationId, ObligationYear, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HydrateDuePlan_ShouldUseTheDueWorkIndexWithoutAnInMemorySort()
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        await OrganisationObligationSummaries.InsertManyAsync(
            [
                new OrganisationObligationSummary
                {
                    OrganisationId = Guid.NewGuid(),
                    ObligationYear = ObligationYear,
                    IsHydrationActive = true,
                    Priority = OrganisationObligationHydrationPriority.NewEligible,
                    NextRefreshAt = utcNow,
                },
                new OrganisationObligationSummary
                {
                    OrganisationId = Guid.NewGuid(),
                    ObligationYear = ObligationYear,
                    IsHydrationActive = true,
                    Priority = OrganisationObligationHydrationPriority.ScheduledRefresh,
                    NextRefreshAt = utcNow,
                },
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );
        var command = new BsonDocument
        {
            ["explain"] = new BsonDocument
            {
                ["find"] = nameof(OrganisationObligationSummary),
                ["filter"] = new BsonDocument
                {
                    ["obligationYear"] = ObligationYear,
                    ["isHydrationActive"] = true,
                    ["nextRefreshAt"] = new BsonDocument("$lte", utcNow),
                },
                ["sort"] = new BsonDocument { ["priority"] = 1, ["nextRefreshAt"] = 1 },
                ["limit"] = 10,
            },
            ["verbosity"] = "queryPlanner",
        };

        var plan = await GetMongoDatabase()
            .RunCommandAsync<BsonDocument>(command, cancellationToken: TestContext.Current.CancellationToken);
        var renderedWinningPlan = plan["queryPlanner"]["winningPlan"].ToJson();

        renderedWinningPlan.Should().Contain(HydrationDueWorkIndexName);
        renderedWinningPlan.Should().NotContain("\"stage\" : \"SORT\"");
    }

    [Fact]
    public async Task HydrateDue_WhenSourceReturnsNoObligations_ShouldPersistReadyEmptySummary()
    {
        var organisationId = Guid.NewGuid();
        await InsertActiveSnapshot();
        await InsertEligibility(organisationId, RegistrationType.DirectProducer);
        ObligationSource.ReadObligations(organisationId, ObligationYear, Arg.Any<CancellationToken>()).Returns([]);
        var subject = CreateSubject();

        await subject.HydrateDue(ObligationYear, TestContext.Current.CancellationToken);

        var summary = await OrganisationObligationSummaries
            .Find(x => x.OrganisationId == organisationId && x.ObligationYear == ObligationYear)
            .SingleAsync(TestContext.Current.CancellationToken);
        summary.ObligationCount.Should().Be(0);
        summary.RecyclingObligationsMet.Should().BeNull();
        summary.ObligationCoveragePercentage.Should().Be(0);
        summary.RefreshState.Should().Be(OrganisationObligationRefreshState.Ready);
    }

    [Fact]
    public async Task HydrateDue_WhenSourceFails_ShouldRetainFailureAndScheduleRetry()
    {
        var organisationId = Guid.NewGuid();
        await InsertActiveSnapshot();
        await InsertEligibility(organisationId, RegistrationType.DirectProducer);
        ObligationSource
            .ReadObligations(organisationId, ObligationYear, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IEnumerable<PrnObligation>>(new HttpRequestException("PRN is unavailable")));
        var subject = CreateSubject();

        var processedCount = await subject.HydrateDue(ObligationYear, TestContext.Current.CancellationToken);

        processedCount.Should().Be(1);
        var summary = await OrganisationObligationSummaries
            .Find(x => x.OrganisationId == organisationId && x.ObligationYear == ObligationYear)
            .SingleAsync(TestContext.Current.CancellationToken);
        summary.RefreshState.Should().Be(OrganisationObligationRefreshState.Failed);
        summary.LastSuccessfulReadAt.Should().BeNull();
        summary.LastFailure.Should().Be("PRN is unavailable");
        summary.Priority.Should().Be(OrganisationObligationHydrationPriority.Retry);
        summary.AttemptCount.Should().Be(1);
        summary.NextRefreshAt.Should().Be(_timeProvider.GetUtcNow().AddMinutes(1).UtcDateTime);
        HydrationMetrics.Received(1).Failed();
    }

    [Fact]
    public async Task HydrateDue_WhenAnActiveSummaryHasNoSuccessfulReadWithinTheThreshold_ShouldRecordStaleness()
    {
        var organisationId = Guid.NewGuid();
        var requestedAt = _timeProvider.GetUtcNow().AddHours(-2).UtcDateTime;
        await InsertActiveSnapshot();
        await InsertEligibility(organisationId, RegistrationType.DirectProducer);
        await InsertSummary(
            organisationId,
            OrganisationObligationRefreshState.Pending,
            null,
            isHydrationActive: true,
            nextRefreshAt: _timeProvider.GetUtcNow().AddHours(1).UtcDateTime,
            requestedAt: requestedAt
        );
        var subject = CreateSubject(
            new OrganisationObligationHydrationOptions { MaximumSummaryStaleness = TimeSpan.FromHours(1) }
        );

        await subject.HydrateDue(ObligationYear, TestContext.Current.CancellationToken);

        HydrationMetrics
            .Received(1)
            .StalenessObserved(
                1,
                Arg.Is<double>(x =>
                    x >= TimeSpan.FromHours(2).TotalSeconds - 1 && x <= TimeSpan.FromHours(2).TotalSeconds + 1
                )
            );
    }

    [Fact]
    public async Task HydrateDue_WhenAnActiveSummaryHasAnOldSuccessfulRead_ShouldRecordStaleness()
    {
        var organisationId = Guid.NewGuid();
        await InsertActiveSnapshot();
        await InsertEligibility(organisationId, RegistrationType.DirectProducer);
        await InsertSummary(
            organisationId,
            OrganisationObligationRefreshState.Ready,
            _timeProvider.GetUtcNow().AddHours(-2).UtcDateTime,
            isHydrationActive: true,
            nextRefreshAt: _timeProvider.GetUtcNow().AddHours(1).UtcDateTime
        );
        var subject = CreateSubject(
            new OrganisationObligationHydrationOptions { MaximumSummaryStaleness = TimeSpan.FromHours(1) }
        );

        await subject.HydrateDue(ObligationYear, TestContext.Current.CancellationToken);

        HydrationMetrics
            .Received(1)
            .StalenessObserved(
                1,
                Arg.Is<double>(x =>
                    x >= TimeSpan.FromHours(2).TotalSeconds - 1 && x <= TimeSpan.FromHours(2).TotalSeconds + 1
                )
            );
    }

    [Fact]
    public async Task HydrateDue_WhenRefreshFailsAfterAPreviousSuccess_ShouldRetainTheLastMetrics()
    {
        var organisationId = Guid.NewGuid();
        var successfulReadAt = _timeProvider.GetUtcNow().AddMinutes(-30).UtcDateTime;
        await InsertActiveSnapshot();
        await InsertEligibility(organisationId, RegistrationType.DirectProducer);
        await InsertSummary(
            organisationId,
            OrganisationObligationRefreshState.Ready,
            successfulReadAt,
            isHydrationActive: true
        );
        ObligationSource
            .ReadObligations(organisationId, ObligationYear, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IEnumerable<PrnObligation>>(new HttpRequestException("PRN is unavailable")));
        var subject = CreateSubject();

        await subject.HydrateDue(ObligationYear, TestContext.Current.CancellationToken);

        var summary = await OrganisationObligationSummaries
            .Find(x => x.OrganisationId == organisationId && x.ObligationYear == ObligationYear)
            .SingleAsync(TestContext.Current.CancellationToken);
        summary.RefreshState.Should().Be(OrganisationObligationRefreshState.Failed);
        summary.LastSuccessfulReadAt.Should().Be(successfulReadAt);
        summary.TotalAcceptedTonnage.Should().Be(4);
        summary.TotalObligatedTonnage.Should().Be(5);
        summary.ObligationCoveragePercentage.Should().Be(80);
    }

    private OrganisationObligationHydrationService CreateSubject(
        OrganisationObligationHydrationOptions? hydrationOptions = null
    )
    {
        var database = GetMongoDatabase();
        var dbContext = new MongoDbContext(
            database,
            Options.Create(new MongoDbOptions()),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<MongoDbContext>>()
        );

        var options = Options.Create(hydrationOptions ?? new OrganisationObligationHydrationOptions());

        return new OrganisationObligationHydrationService(
            dbContext,
            ObligationSource,
            new OrganisationObligationRequestPacer(options, _timeProvider),
            HydrationMetrics,
            options,
            _timeProvider,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrganisationObligationHydrationService>.Instance
        );
    }

    private Task InsertActiveSnapshot(string activeGeneration = "active") =>
        OrganisationEligibilitySnapshots.InsertOneAsync(
            new OrganisationEligibilitySnapshot
            {
                Id = OrganisationEligibilitySnapshot.SnapshotId,
                ActiveGeneration = activeGeneration,
                ActiveContentFingerprint = "fingerprint",
                ActiveRowCount = 1,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

    private Task InsertEligibility(
        Guid organisationId,
        RegistrationType registrationType,
        int obligationYear = ObligationYear,
        OrganisationRegistrationStatus registrationStatus = OrganisationRegistrationStatus.Registered,
        OrganisationReferenceNumberResolutionState referenceResolutionState =
            OrganisationReferenceNumberResolutionState.Resolved,
        string generation = "active"
    ) =>
        OrganisationComplianceDeclarationEligibilities.InsertOneAsync(
            new OrganisationComplianceDeclarationEligibility
            {
                Generation = generation,
                OrganisationId = organisationId,
                ObligationYear = obligationYear,
                RegistrationType = registrationType,
                RegistrationStatus = registrationStatus,
                Name = "Organisation",
                ReferenceNumber = "reference",
                ReferenceNumberResolutionState = referenceResolutionState,
                SourceFingerprint = "fingerprint",
                RefreshedAt = _timeProvider.GetUtcNow().UtcDateTime,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

    private Task InsertSummary(
        Guid organisationId,
        OrganisationObligationRefreshState refreshState,
        DateTime? lastSuccessfulReadAt,
        bool isHydrationActive = false,
        DateTime? nextRefreshAt = null,
        DateTime? requestedAt = null
    ) =>
        OrganisationObligationSummaries.InsertOneAsync(
            new OrganisationObligationSummary
            {
                OrganisationId = organisationId,
                ObligationYear = ObligationYear,
                ObligationCount = 1,
                TotalAcceptedTonnage = 4,
                TotalObligatedTonnage = 5,
                RecyclingObligationsMet = true,
                ObligationCoveragePercentage = 80,
                SourceFingerprint = "summary-fingerprint",
                LastSuccessfulReadAt = lastSuccessfulReadAt,
                LastAttemptedAt = _timeProvider.GetUtcNow().UtcDateTime,
                NextRefreshAt = nextRefreshAt ?? _timeProvider.GetUtcNow().UtcDateTime,
                Priority = OrganisationObligationHydrationPriority.ScheduledRefresh,
                RequestedAt = requestedAt ?? _timeProvider.GetUtcNow().UtcDateTime,
                IsHydrationActive = isHydrationActive,
                RefreshState = refreshState,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

    private Task InsertHydrationSummary(
        Guid organisationId,
        DateTime? nextRefreshAt = null,
        DateTime? lastSuccessfulReadAt = null
    ) =>
        OrganisationObligationSummaries.InsertOneAsync(
            new OrganisationObligationSummary
            {
                OrganisationId = organisationId,
                ObligationYear = ObligationYear,
                Priority = OrganisationObligationHydrationPriority.ScheduledRefresh,
                NextRefreshAt = nextRefreshAt ?? _timeProvider.GetUtcNow().UtcDateTime,
                RequestedAt = _timeProvider.GetUtcNow().UtcDateTime,
                LastSuccessfulReadAt = lastSuccessfulReadAt,
                RefreshState = OrganisationObligationRefreshState.Pending,
                IsHydrationActive = true,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

    private static PrnObligation CreateObligation(string materialName, int accepted, int obligated, string status) =>
        ObligationFixture
            .Default()
            .With(x => x.MaterialName, materialName)
            .With(x => x.TonnageAccepted, accepted)
            .With(x => x.ObligationToMeet, obligated)
            .With(x => x.Status, status)
            .Create();
}
