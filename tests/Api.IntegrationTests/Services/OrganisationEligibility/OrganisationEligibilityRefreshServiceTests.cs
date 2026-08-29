using System.Text.Json;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.IntegrationTests.Infrastructure;
using Defra.WasteObligations.Api.Services;
using Defra.WasteObligations.Api.Services.AccountBackend;
using Defra.WasteObligations.Api.Services.OrganisationEligibility;
using Defra.WasteObligations.Api.Services.WasteOrganisations;
using Defra.WasteObligations.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using NSubstitute;
using Organisation = Defra.WasteObligations.Api.Services.WasteOrganisations.Organisation;
using OrganisationComplianceDeclarationEligibilityEntity = Defra.WasteObligations.Api.Data.Entities.OrganisationComplianceDeclarationEligibility;
using Registration = Defra.WasteObligations.Api.Services.WasteOrganisations.Registration;
using WasteOrganisationsAddress = Defra.WasteObligations.Api.Services.WasteOrganisations.Address;
using WasteOrganisationsRegistrationStatus = Defra.WasteObligations.Api.Services.WasteOrganisations.RegistrationStatus;
using WasteOrganisationsRegistrationType = Defra.WasteObligations.Api.Services.WasteOrganisations.RegistrationType;

namespace Defra.WasteObligations.Api.IntegrationTests.Services.OrganisationEligibility;

public class OrganisationEligibilityRefreshServiceTests : IntegrationTestBase
{
    private const string ExpiredGenerationIndexName = "RefreshedAt";
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
    private IOrganisationReferenceSearchService OrganisationReferenceSearchService { get; } =
        Substitute.For<IOrganisationReferenceSearchService>();
    private IOrganisationEligibilitySource OrganisationEligibilitySource { get; } =
        Substitute.For<IOrganisationEligibilitySource>();

    [Fact]
    public async Task Refresh_WhenNoActiveSnapshot_ShouldPromoteResolvedRows()
    {
        var organisationId = Guid.NewGuid();
        ArrangeSource(organisationId);
        ArrangeDirectProducerReference(organisationId, "051829");
        var subject = CreateSubject();

        var result = await subject.Refresh(TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(OrganisationEligibilityRefreshOutcome.Promoted);
        result.ActiveGeneration.Should().NotBeNullOrWhiteSpace();
        result.RowCount.Should().Be(1);
        var snapshot = await OrganisationEligibilitySnapshots
            .Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleAsync(TestContext.Current.CancellationToken);
        snapshot.ActiveGeneration.Should().Be(result.ActiveGeneration);
        snapshot.ActiveContentFingerprint.Should().Be(result.ContentFingerprint);
        var row = await OrganisationComplianceDeclarationEligibilities
            .Find(x => x.Generation == result.ActiveGeneration)
            .SingleAsync(TestContext.Current.CancellationToken);
        row.ReferenceNumber.Should().Be("051829");
        row.ReferenceNumberResolutionState.Should().Be(OrganisationReferenceNumberResolutionState.Resolved);
        var persistedProjection = JsonSerializer.Serialize(
            new
            {
                Snapshot = snapshot with
                {
                    ActiveGeneration = "{Generated}",
                    ActiveContentFingerprint = "{Calculated}",
                },
                Row = row with
                {
                    Generation = "{Generated}",
                    SourceFingerprint = "{Calculated}",
                },
            },
            JsonSerializerOptions.Web
        );
        await VerifyJson(persistedProjection).ScrubMembers("id");
    }

    [Fact]
    public async Task Refresh_WhenTimeHasSubMillisecondPrecision_ShouldPersistMillisecondPrecision()
    {
        var utcNow = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234);
        _timeProvider.SetUtcNow(utcNow);
        var organisationId = Guid.NewGuid();
        ArrangeSource(organisationId);
        ArrangeDirectProducerReference(organisationId, "051829");
        var subject = CreateSubject();

        var result = await subject.Refresh(TestContext.Current.CancellationToken);

        var snapshot = await OrganisationEligibilitySnapshots
            .Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleAsync(TestContext.Current.CancellationToken);
        var row = await OrganisationComplianceDeclarationEligibilities
            .Find(x => x.Generation == result.ActiveGeneration)
            .SingleAsync(TestContext.Current.CancellationToken);
        var expectedUtcNow = utcNow.AddTicks(-utcNow.Ticks % TimeSpan.TicksPerMillisecond).UtcDateTime;

        snapshot.ActiveGenerationPromotedAt.Should().Be(expectedUtcNow);
        snapshot.LastVerifiedAt.Should().Be(expectedUtcNow);
        row.DeclarationStateUpdatedAt.Should().Be(expectedUtcNow);
        row.RefreshedAt.Should().Be(expectedUtcNow);
    }

    [Fact]
    public async Task Refresh_WhenCurrentObligationSummaryExists_ShouldCopyMetricsToThePromotedRows()
    {
        var organisationId = Guid.NewGuid();
        ArrangeSource(organisationId);
        ArrangeDirectProducerReference(organisationId, "051829");
        await OrganisationObligationSummaries.InsertOneAsync(
            new OrganisationObligationSummary
            {
                OrganisationId = organisationId,
                ObligationYear = 2026,
                RecyclingObligationsMet = false,
                ObligationCoveragePercentage = 80,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        var subject = CreateSubject();

        var result = await subject.Refresh(TestContext.Current.CancellationToken);

        var row = await OrganisationComplianceDeclarationEligibilities
            .Find(x => x.Generation == result.ActiveGeneration)
            .SingleAsync(TestContext.Current.CancellationToken);
        row.RecyclingObligationsMet.Should().BeFalse();
        row.ObligationCoveragePercentage.Should().Be(80);
    }

    [Fact]
    public async Task Refresh_WhenCollectingExpiredGenerations_ShouldUseTheRefreshedAtIndex()
    {
        var organisationId = Guid.NewGuid();
        ArrangeSource(organisationId);
        ArrangeDirectProducerReference(organisationId, "051829");
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        await OrganisationComplianceDeclarationEligibilities.InsertManyAsync(
            [
                CreateEligibility("expired", Guid.NewGuid(), utcNow.AddDays(-31)),
                .. Enumerable
                    .Range(0, 100)
                    .Select(_ => CreateEligibility("retained", Guid.NewGuid(), utcNow.AddDays(-1))),
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );
        var subject = CreateSubject();

        await using var profiler = await MongoQueryProfiler.Start(
            GetMongoDatabase(),
            [MongoQueryProfiler.IntegrationTestApplicationName],
            TestContext.Current.CancellationToken
        );
        await subject.Refresh(TestContext.Current.CancellationToken);
        var profile = await profiler.Stop(TestContext.Current.CancellationToken);

        profile.QueriesWithoutAnIndex.Should().BeEmpty();
        profile
            .Queries.Should()
            .Contain(x =>
                x.Namespace == "waste-obligations.OrganisationComplianceDeclarationEligibility"
                && x.IndexNames.Contains(ExpiredGenerationIndexName)
            );
    }

    [Fact]
    public async Task Refresh_WhenInitialReferenceLookupFails_ShouldNotPromoteGeneration()
    {
        var organisationId = Guid.NewGuid();
        ArrangeSource(organisationId);
        var unsubmittedEligibilityVisibilityService = Substitute.For<IUnsubmittedEligibilityVisibilityService>();
        OrganisationReferenceSearchService
            .SearchOrganisationsByExternalIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromException<OrganisationsByExternalIdsResponse>(
                    new HttpRequestException("Account is unavailable")
                )
            );
        var subject = CreateSubject(
            GetMongoDatabase(),
            OrganisationEligibilitySource,
            OrganisationReferenceSearchService,
            _timeProvider,
            unsubmittedEligibilityVisibilityService: unsubmittedEligibilityVisibilityService
        );

        var act = async () => await subject.Refresh(TestContext.Current.CancellationToken);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("Initial organisation eligibility generation contains failed Account reference lookups");
        (
            await OrganisationComplianceDeclarationEligibilities.CountDocumentsAsync(
                Builders<OrganisationComplianceDeclarationEligibilityEntity>.Filter.Empty,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(0);
        await unsubmittedEligibilityVisibilityService
            .DidNotReceive()
            .Apply(
                Arg.Any<IReadOnlyList<OrganisationComplianceDeclarationEligibility>>(),
                Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>()
            );
        (
            await OrganisationEligibilitySnapshots.CountDocumentsAsync(
                Builders<OrganisationEligibilitySnapshot>.Filter.Empty,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(0);
    }

    [Fact]
    public async Task Refresh_WhenMultipleOrganisationsAreNew_ShouldPromoteEveryRow()
    {
        var firstOrganisationId = Guid.NewGuid();
        var secondOrganisationId = Guid.NewGuid();
        OrganisationEligibilitySource
            .Search(Arg.Any<CancellationToken>())
            .Returns(
                new OrganisationSearch
                {
                    Organisations =
                    [
                        CreateSourceOrganisation(firstOrganisationId, "First organisation"),
                        CreateSourceOrganisation(secondOrganisationId, "Second organisation"),
                    ],
                }
            );
        OrganisationReferenceSearchService
            .SearchOrganisationsByExternalIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(
                new OrganisationsByExternalIdsResponse
                {
                    Organisations =
                    [
                        new AccountOrganisation
                        {
                            ExternalId = firstOrganisationId.ToString("D"),
                            ReferenceNumber = "051829",
                        },
                        new AccountOrganisation
                        {
                            ExternalId = secondOrganisationId.ToString("D"),
                            ReferenceNumber = "051830",
                        },
                    ],
                }
            );
        var subject = CreateSubject();

        var result = await subject.Refresh(TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(OrganisationEligibilityRefreshOutcome.Promoted);
        result.RowCount.Should().Be(2);
        var rows = await OrganisationComplianceDeclarationEligibilities
            .Find(Builders<OrganisationComplianceDeclarationEligibilityEntity>.Filter.Empty)
            .ToListAsync(TestContext.Current.CancellationToken);
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(x => x.Id != ObjectId.Empty);
    }

    [Fact]
    public async Task Refresh_WhenGenerationIsPromoted_ShouldLogWrittenDocumentCountAndDuration()
    {
        var organisationId = Guid.NewGuid();
        ArrangeSource(organisationId);
        ArrangeDirectProducerReference(organisationId, "051829");
        var logger = new RecordingLogger<OrganisationEligibilityRefreshService>();
        var subject = CreateSubject(
            GetMongoDatabase(),
            OrganisationEligibilitySource,
            OrganisationReferenceSearchService,
            _timeProvider,
            logger
        );

        await subject.Refresh(TestContext.Current.CancellationToken);

        logger
            .Entries.Should()
            .ContainSingle(x => x.Level == LogLevel.Information && x.Message.Contains("wrote 1 documents in"));
    }

    [Fact]
    public async Task Refresh_WhenContentIsUnchanged_ShouldVerifyWithoutWritingAnotherGeneration()
    {
        var organisationId = Guid.NewGuid();
        ArrangeSource(organisationId);
        ArrangeDirectProducerReference(organisationId, "051829");
        var subject = CreateSubject();
        var initial = await subject.Refresh(TestContext.Current.CancellationToken);
        _timeProvider.Advance(TimeSpan.FromMinutes(30));

        var refreshed = await subject.Refresh(TestContext.Current.CancellationToken);

        refreshed.Outcome.Should().Be(OrganisationEligibilityRefreshOutcome.Unchanged);
        refreshed.ActiveGeneration.Should().Be(initial.ActiveGeneration);
        (
            await OrganisationComplianceDeclarationEligibilities.CountDocumentsAsync(
                Builders<OrganisationComplianceDeclarationEligibilityEntity>.Filter.Empty,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(1);
        var snapshot = await OrganisationEligibilitySnapshots
            .Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleAsync(TestContext.Current.CancellationToken);
        snapshot.LastVerifiedAt.Should().Be(_timeProvider.GetUtcNow().UtcDateTime);
        await OrganisationReferenceSearchService
            .Received(1)
            .SearchOrganisationsByExternalIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_WhenSourceRowChanges_ShouldPromoteACompleteNewGenerationWithoutAnotherReferenceLookup()
    {
        var organisationId = Guid.NewGuid();
        ArrangeSource(organisationId);
        ArrangeDirectProducerReference(organisationId, "051829");
        var subject = CreateSubject();
        var initial = await subject.Refresh(TestContext.Current.CancellationToken);
        _timeProvider.Advance(TimeSpan.FromMinutes(30));
        ArrangeSource(organisationId, name: "Changed organisation name");

        var refreshed = await subject.Refresh(TestContext.Current.CancellationToken);

        refreshed.Outcome.Should().Be(OrganisationEligibilityRefreshOutcome.Promoted);
        refreshed.ActiveGeneration.Should().NotBe(initial.ActiveGeneration);
        (
            await OrganisationComplianceDeclarationEligibilities.CountDocumentsAsync(
                Builders<OrganisationComplianceDeclarationEligibilityEntity>.Filter.Empty,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(2);
        var activeRow = await OrganisationComplianceDeclarationEligibilities
            .Find(x => x.Generation == refreshed.ActiveGeneration)
            .SingleAsync(TestContext.Current.CancellationToken);
        activeRow.Name.Should().Be("Changed organisation name");
        activeRow.ReferenceNumber.Should().Be("051829");
        var snapshot = await OrganisationEligibilitySnapshots
            .Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleAsync(TestContext.Current.CancellationToken);
        snapshot.RetainedGenerations.Should().ContainSingle();
        snapshot.RetainedGenerations[0].Generation.Should().Be(initial.ActiveGeneration);
        snapshot.RetainedGenerations[0].DeleteAfter.Should().Be(_timeProvider.GetUtcNow().UtcDateTime.AddDays(30));
        await OrganisationReferenceSearchService
            .Received(1)
            .SearchOrganisationsByExternalIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_WhenPromotingANewGeneration_ShouldRetainTheMaterialisedStateVersion()
    {
        var organisationId = Guid.NewGuid();
        ArrangeSource(organisationId);
        ArrangeDirectProducerReference(organisationId, "051829");
        var subject = CreateSubject();
        await subject.Refresh(TestContext.Current.CancellationToken);
        await OrganisationEligibilitySnapshots.UpdateOneAsync(
            x => x.Id == OrganisationEligibilitySnapshot.SnapshotId,
            Builders<OrganisationEligibilitySnapshot>.Update.Inc(x => x.MaterialisedStateVersion, 3),
            cancellationToken: TestContext.Current.CancellationToken
        );
        ArrangeSource(organisationId, name: "Changed organisation name");

        await subject.Refresh(TestContext.Current.CancellationToken);

        var snapshot = await OrganisationEligibilitySnapshots
            .Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleAsync(TestContext.Current.CancellationToken);
        snapshot.MaterialisedStateVersion.Should().Be(3);
    }

    [Fact]
    public async Task Refresh_WhenSeveralGenerationsArePromotedWithinRetentionPeriod_ShouldRetainEachSupersededGeneration()
    {
        var organisationId = Guid.NewGuid();
        ArrangeSource(organisationId);
        ArrangeDirectProducerReference(organisationId, "051829");
        var subject = CreateSubject();
        var initial = await subject.Refresh(TestContext.Current.CancellationToken);
        _timeProvider.Advance(TimeSpan.FromDays(1));
        ArrangeSource(organisationId, name: "First changed name");
        var second = await subject.Refresh(TestContext.Current.CancellationToken);
        var initialDeleteAfter = _timeProvider.GetUtcNow().UtcDateTime.AddDays(30);
        _timeProvider.Advance(TimeSpan.FromDays(1));
        ArrangeSource(organisationId, name: "Second changed name");

        var third = await subject.Refresh(TestContext.Current.CancellationToken);

        third.Outcome.Should().Be(OrganisationEligibilityRefreshOutcome.Promoted);
        var snapshot = await OrganisationEligibilitySnapshots
            .Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleAsync(TestContext.Current.CancellationToken);
        snapshot
            .RetainedGenerations.Select(x => x.Generation)
            .Should()
            .Equal(initial.ActiveGeneration, second.ActiveGeneration);
        snapshot
            .RetainedGenerations.Select(x => x.DeleteAfter)
            .Should()
            .Equal(initialDeleteAfter, _timeProvider.GetUtcNow().UtcDateTime.AddDays(30));
        (
            await OrganisationComplianceDeclarationEligibilities.CountDocumentsAsync(
                Builders<OrganisationComplianceDeclarationEligibilityEntity>.Filter.Empty,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(3);
    }

    [Fact]
    public async Task Refresh_WhenRetainedGenerationExpires_ShouldGarbageCollectIt()
    {
        var organisationId = Guid.NewGuid();
        ArrangeSource(organisationId);
        ArrangeDirectProducerReference(organisationId, "051829");
        var subject = CreateSubject();
        var initial = await subject.Refresh(TestContext.Current.CancellationToken);
        ArrangeSource(organisationId, name: "Changed organisation name");
        var promoted = await subject.Refresh(TestContext.Current.CancellationToken);
        _timeProvider.Advance(TimeSpan.FromDays(30));

        var refreshed = await subject.Refresh(TestContext.Current.CancellationToken);

        refreshed.Outcome.Should().Be(OrganisationEligibilityRefreshOutcome.Unchanged);
        refreshed.ActiveGeneration.Should().Be(promoted.ActiveGeneration);
        (
            await OrganisationComplianceDeclarationEligibilities.CountDocumentsAsync(
                x => x.Generation == initial.ActiveGeneration,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(0);
        (
            await OrganisationComplianceDeclarationEligibilities.CountDocumentsAsync(
                x => x.Generation == promoted.ActiveGeneration,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(1);
        var snapshot = await OrganisationEligibilitySnapshots
            .Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleAsync(TestContext.Current.CancellationToken);
        snapshot.RetainedGenerations.Should().BeEmpty();
    }

    [Fact]
    public async Task Refresh_WhenOrphanedGenerationExpires_ShouldGarbageCollectItAndPreserveRecentAndActiveRows()
    {
        var organisationId = Guid.NewGuid();
        ArrangeSource(organisationId);
        ArrangeDirectProducerReference(organisationId, "051829");
        var subject = CreateSubject();
        var initial = await subject.Refresh(TestContext.Current.CancellationToken);
        var activeRow = await OrganisationComplianceDeclarationEligibilities
            .Find(x => x.Generation == initial.ActiveGeneration)
            .SingleAsync(TestContext.Current.CancellationToken);
        var expiredAt = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-31);
        await OrganisationComplianceDeclarationEligibilities.ReplaceOneAsync(
            x => x.Id == activeRow.Id,
            activeRow with
            {
                RefreshedAt = expiredAt,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        const string expiredGeneration = "expired-orphan";
        const string recentGeneration = "recent-orphan";
        await OrganisationComplianceDeclarationEligibilities.InsertManyAsync(
            [
                activeRow with
                {
                    Id = ObjectId.GenerateNewId(),
                    Generation = expiredGeneration,
                    RefreshedAt = expiredAt,
                },
                activeRow with
                {
                    Id = ObjectId.GenerateNewId(),
                    Generation = recentGeneration,
                    RefreshedAt = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-29),
                },
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );

        await subject.Refresh(TestContext.Current.CancellationToken);

        var generations = await OrganisationComplianceDeclarationEligibilities
            .Find(Builders<OrganisationComplianceDeclarationEligibilityEntity>.Filter.Empty)
            .Project(x => x.Generation)
            .ToListAsync(TestContext.Current.CancellationToken);
        generations.Should().BeEquivalentTo(initial.ActiveGeneration, recentGeneration);
        generations.Should().NotContain(expiredGeneration);
    }

    [Fact]
    public async Task Refresh_WhenAnotherHostCreatesTheInitialSnapshot_ShouldNotReplaceIt()
    {
        const string competingGeneration = "competing-generation";
        var competingSnapshot = CreateSnapshot(competingGeneration, "competing-fingerprint");
        var competingDatabase = GetMongoDatabase();
        var snapshotInserted = 0;
        var monitoredDatabase = CreateMonitoredDatabase(@event =>
        {
            if (
                !IsCommandForCollection(@event, "insert", nameof(OrganisationEligibilitySnapshot))
                || Interlocked.Exchange(ref snapshotInserted, 1) != 0
            )
            {
                return;
            }

            competingDatabase
                .GetCollection<OrganisationEligibilitySnapshot>(nameof(OrganisationEligibilitySnapshot))
                .InsertOne(competingSnapshot);
        });
        var organisationId = Guid.NewGuid();
        var source = CreateSource(organisationId);
        var referenceSearchService = Substitute.For<IOrganisationReferenceSearchService>();
        ArrangeDirectProducerReference(referenceSearchService, organisationId, "051829");
        var subject = CreateSubject(monitoredDatabase, source, referenceSearchService, _timeProvider);

        var act = async () => await subject.Refresh(TestContext.Current.CancellationToken);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("The active organisation eligibility generation changed during refresh");
        snapshotInserted.Should().Be(1);
        var snapshot = await OrganisationEligibilitySnapshots
            .Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleAsync(TestContext.Current.CancellationToken);
        snapshot.ActiveGeneration.Should().Be(competingGeneration);
        (
            await OrganisationComplianceDeclarationEligibilities.CountDocumentsAsync(
                Builders<OrganisationComplianceDeclarationEligibilityEntity>.Filter.Empty,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(0);
    }

    [Fact]
    public async Task Refresh_WhenActiveGenerationChangesBeforePromotion_ShouldNotReplaceIt()
    {
        const string initialGeneration = "initial-generation";
        const string competingGeneration = "competing-generation";
        await OrganisationEligibilitySnapshots.InsertOneAsync(
            CreateSnapshot(initialGeneration, "initial-fingerprint"),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var competingSnapshot = CreateSnapshot(competingGeneration, "competing-fingerprint");
        var competingDatabase = GetMongoDatabase();
        var snapshotReplaced = 0;
        var monitoredDatabase = CreateMonitoredDatabase(@event =>
        {
            if (
                !IsCommandForCollection(@event, "update", nameof(OrganisationEligibilitySnapshot))
                || Interlocked.Exchange(ref snapshotReplaced, 1) != 0
            )
            {
                return;
            }

            competingDatabase
                .GetCollection<OrganisationEligibilitySnapshot>(nameof(OrganisationEligibilitySnapshot))
                .ReplaceOne(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId, competingSnapshot);
        });
        var organisationId = Guid.NewGuid();
        var source = CreateSource(organisationId);
        var referenceSearchService = Substitute.For<IOrganisationReferenceSearchService>();
        ArrangeDirectProducerReference(referenceSearchService, organisationId, "051829");
        var subject = CreateSubject(monitoredDatabase, source, referenceSearchService, _timeProvider);

        var act = async () => await subject.Refresh(TestContext.Current.CancellationToken);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("The active organisation eligibility generation changed during refresh");
        snapshotReplaced.Should().Be(1);
        var snapshot = await OrganisationEligibilitySnapshots
            .Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleAsync(TestContext.Current.CancellationToken);
        snapshot.ActiveGeneration.Should().Be(competingGeneration);
    }

    [Fact]
    public async Task Refresh_WhenMaterialisedStateChangesBeforePromotion_ShouldNotReplaceIt()
    {
        var organisationId = Guid.NewGuid();
        ArrangeSource(organisationId);
        ArrangeDirectProducerReference(organisationId, "051829");
        var initial = await CreateSubject().Refresh(TestContext.Current.CancellationToken);
        ArrangeSource(organisationId, name: "Changed organisation name");
        var competingDatabase = GetMongoDatabase();
        var stateVersionUpdated = 0;
        var monitoredDatabase = CreateMonitoredDatabase(@event =>
        {
            if (
                !IsCommandForCollection(@event, "update", nameof(OrganisationEligibilitySnapshot))
                || Interlocked.Exchange(ref stateVersionUpdated, 1) != 0
            )
            {
                return;
            }

            competingDatabase
                .GetCollection<OrganisationEligibilitySnapshot>(nameof(OrganisationEligibilitySnapshot))
                .UpdateOne(
                    x => x.Id == OrganisationEligibilitySnapshot.SnapshotId,
                    Builders<OrganisationEligibilitySnapshot>.Update.Inc(x => x.MaterialisedStateVersion, 1)
                );
        });
        var subject = CreateSubject(
            monitoredDatabase,
            OrganisationEligibilitySource,
            OrganisationReferenceSearchService,
            _timeProvider
        );

        var act = async () => await subject.Refresh(TestContext.Current.CancellationToken);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("The active organisation eligibility generation changed during refresh");
        stateVersionUpdated.Should().Be(1);
        var snapshot = await OrganisationEligibilitySnapshots
            .Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleAsync(TestContext.Current.CancellationToken);
        snapshot.ActiveGeneration.Should().Be(initial.ActiveGeneration);
        snapshot.MaterialisedStateVersion.Should().Be(1);
    }

    [Fact]
    public async Task Refresh_WhenActiveGenerationChangesBeforeVerification_ShouldNotUpdateIt()
    {
        var organisationId = Guid.NewGuid();
        ArrangeSource(organisationId);
        ArrangeDirectProducerReference(organisationId, "051829");
        var initial = await CreateSubject().Refresh(TestContext.Current.CancellationToken);
        _timeProvider.Advance(TimeSpan.FromMinutes(30));
        const string competingGeneration = "competing-generation";
        var competingVerifiedAt = _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-5);
        var competingSnapshot = CreateSnapshot(competingGeneration, initial.ContentFingerprint, competingVerifiedAt);
        var competingDatabase = GetMongoDatabase();
        var snapshotReplaced = 0;
        var monitoredDatabase = CreateMonitoredDatabase(@event =>
        {
            if (
                !IsCommandForCollection(@event, "update", nameof(OrganisationEligibilitySnapshot))
                || Interlocked.Exchange(ref snapshotReplaced, 1) != 0
            )
            {
                return;
            }

            competingDatabase
                .GetCollection<OrganisationEligibilitySnapshot>(nameof(OrganisationEligibilitySnapshot))
                .ReplaceOne(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId, competingSnapshot);
        });
        var subject = CreateSubject(
            monitoredDatabase,
            OrganisationEligibilitySource,
            OrganisationReferenceSearchService,
            _timeProvider
        );

        var act = async () => await subject.Refresh(TestContext.Current.CancellationToken);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("The active organisation eligibility generation changed during refresh");
        snapshotReplaced.Should().Be(1);
        var snapshot = await OrganisationEligibilitySnapshots
            .Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleAsync(TestContext.Current.CancellationToken);
        snapshot.ActiveGeneration.Should().Be(competingGeneration);
        snapshot.LastVerifiedAt.Should().Be(competingVerifiedAt);
    }

    private OrganisationEligibilityRefreshService CreateSubject() =>
        CreateSubject(
            GetMongoDatabase(),
            OrganisationEligibilitySource,
            OrganisationReferenceSearchService,
            _timeProvider
        );

    private static OrganisationEligibilityRefreshService CreateSubject(
        IMongoDatabase database,
        IOrganisationEligibilitySource source,
        IOrganisationReferenceSearchService referenceSearchService,
        TimeProvider timeProvider,
        ILogger<OrganisationEligibilityRefreshService>? logger = null,
        IUnsubmittedEligibilityVisibilityService? unsubmittedEligibilityVisibilityService = null
    )
    {
        var dbContext = new MongoDbContext(
            database,
            Options.Create(new MongoDbOptions()),
            Substitute.For<Microsoft.Extensions.Logging.ILogger<MongoDbContext>>()
        );
        var options = Options.Create(new OrganisationEligibilityOptions { AccountReferenceNumberBatchSize = 10 });
        var referenceResolver = new OrganisationReferenceResolver(
            referenceSearchService,
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrganisationReferenceResolver>.Instance
        );

        return new OrganisationEligibilityRefreshService(
            dbContext,
            source,
            referenceResolver,
            unsubmittedEligibilityVisibilityService ?? new UnsubmittedEligibilityVisibilityService(dbContext),
            options,
            timeProvider,
            logger
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OrganisationEligibilityRefreshService>.Instance
        );
    }

    private void ArrangeSource(Guid organisationId, string name = "Example organisation") =>
        OrganisationEligibilitySource
            .Search(Arg.Any<CancellationToken>())
            .Returns(new OrganisationSearch { Organisations = [CreateSourceOrganisation(organisationId, name)] });

    private static IOrganisationEligibilitySource CreateSource(
        Guid organisationId,
        string name = "Example organisation"
    )
    {
        var source = Substitute.For<IOrganisationEligibilitySource>();
        source
            .Search(Arg.Any<CancellationToken>())
            .Returns(new OrganisationSearch { Organisations = [CreateSourceOrganisation(organisationId, name)] });

        return source;
    }

    private static Organisation CreateSourceOrganisation(Guid organisationId, string name) =>
        new()
        {
            Id = organisationId,
            Name = name,
            Address = new WasteOrganisationsAddress(),
            Registrations =
            [
                new Registration
                {
                    Type = WasteOrganisationsRegistrationType.LargeProducer,
                    Status = WasteOrganisationsRegistrationStatus.Registered,
                    RegistrationYear = 2026,
                },
            ],
        };

    private void ArrangeDirectProducerReference(Guid organisationId, string referenceNumber) =>
        ArrangeDirectProducerReference(OrganisationReferenceSearchService, organisationId, referenceNumber);

    private static void ArrangeDirectProducerReference(
        IOrganisationReferenceSearchService referenceSearchService,
        Guid organisationId,
        string referenceNumber
    ) =>
        referenceSearchService
            .SearchOrganisationsByExternalIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(
                new OrganisationsByExternalIdsResponse
                {
                    Organisations =
                    [
                        new AccountOrganisation
                        {
                            ExternalId = organisationId.ToString("D"),
                            ReferenceNumber = referenceNumber,
                        },
                    ],
                }
            );

    private OrganisationEligibilitySnapshot CreateSnapshot(
        string generation,
        string fingerprint,
        DateTime? lastVerifiedAt = null
    ) =>
        new()
        {
            Id = OrganisationEligibilitySnapshot.SnapshotId,
            ActiveGeneration = generation,
            ActiveContentFingerprint = fingerprint,
            ActiveRowCount = 1,
            ActiveGenerationPromotedAt = _timeProvider.GetUtcNow().UtcDateTime,
            LastVerifiedAt = lastVerifiedAt ?? _timeProvider.GetUtcNow().UtcDateTime,
        };

    private static OrganisationComplianceDeclarationEligibilityEntity CreateEligibility(
        string generation,
        Guid organisationId,
        DateTime refreshedAt
    ) =>
        new()
        {
            Generation = generation,
            OrganisationId = organisationId,
            ObligationYear = 2026,
            RegistrationType = Defra.WasteObligations.Api.Data.Entities.RegistrationType.DirectProducer,
            RegistrationStatus = OrganisationRegistrationStatus.Registered,
            Name = "Organisation",
            ReferenceNumber = "reference",
            ReferenceNumberResolutionState = OrganisationReferenceNumberResolutionState.Resolved,
            SourceFingerprint = "fingerprint",
            RefreshedAt = refreshedAt,
        };

    private static IMongoDatabase CreateMonitoredDatabase(Action<CommandStartedEvent> commandStarted)
    {
        var settings = MongoClientSettings.FromConnectionString(
            "mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true&readPreference=secondaryPreferred"
        );
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);
        settings.SocketTimeout = TimeSpan.FromSeconds(5);
        settings.ClusterConfigurator = builder => builder.Subscribe(commandStarted);

        return new MongoClient(settings).GetDatabase("waste-obligations");
    }

    private static bool IsCommandForCollection(CommandStartedEvent @event, string commandName, string collectionName) =>
        @event.CommandName == commandName && @event.Command[commandName].AsString == collectionName;
}
