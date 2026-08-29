using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.IntegrationTests.Infrastructure;
using Defra.WasteObligations.Api.Services;
using Defra.WasteObligations.Api.Utils.Logging;
using Defra.WasteObligations.Api.Utils.Metrics;
using Defra.WasteObligations.AuditEvents;
using Defra.WasteObligations.AuditEvents.Data;
using Defra.WasteObligations.AuditEvents.Entities;
using Defra.WasteObligations.Testing.Fakes;
using Defra.WasteObligations.Testing.Fixtures.Entities;
using Microsoft.AspNetCore.HeaderPropagation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace Defra.WasteObligations.Api.IntegrationTests.Services;

public class ComplianceDeclarationServiceTests : IntegrationTestBase
{
    private const string Entity = "compliance_declaration";
    private const string MatchingReferenceNumber = "100245";
    private const string ComplianceDeclarationNamespace = "waste-obligations.ComplianceDeclaration";
    private const string OrganisationSearchFilter =
        "$or:[{organisation.complianceSchemeName:?},{organisation.name:?},{organisation.referenceNumber:?},{organisation.schemeOperatorName:?}]";

    private ComplianceDeclarationService Subject { get; }
    private IComplianceDeclarationMetrics ComplianceDeclarationMetrics { get; }

    public ComplianceDeclarationServiceTests()
    {
        var database = GetMongoApplicationDatabase();
        var auditEventDbContext = new AuditEventDbContext(database);
        var dbContext = CreateDbContext(database);
        var auditEventService = new AuditEventService(
            auditEventDbContext,
            TimeProvider.System,
            new FakeEventIdGenerator()
        );

        ComplianceDeclarationMetrics = Substitute.For<IComplianceDeclarationMetrics>();
        Subject = new(
            dbContext,
            Substitute.For<ILogger<ComplianceDeclarationService>>(),
            TimeProvider.System,
            auditEventService,
            ComplianceDeclarationMetrics,
            TraceIdReader(),
            new UnsubmittedEligibilityVisibilityService(dbContext)
        );
    }

    [Fact]
    public async Task Read_WhenNoComplianceDeclaration_ShouldBeNull()
    {
        var complianceDeclaration = await Subject.Read(
            ObjectId.GenerateNewId().ToString(),
            TestContext.Current.CancellationToken
        );

        complianceDeclaration.Should().BeNull();
    }

    [Fact]
    public async Task Create_WhenInserted_ShouldBeFound()
    {
        var initial = await Subject.Create(
            ComplianceDeclarationFixture.Default().Create(),
            TestContext.Current.CancellationToken
        );

        var retrieved = await Subject.Read(initial.Id.ToString(), TestContext.Current.CancellationToken);
        var auditEvent = await AuditEvents
            .Find(x => x.Sequence == 1)
            .SingleAsync(TestContext.Current.CancellationToken);

        retrieved.Should().NotBeNull();
        retrieved.Should().BeEquivalentTo(initial);
        auditEvent.EventId.Should().Be("01HXYZ00000000000000000001");
        auditEvent.Entity.Should().Be(Entity);
        auditEvent.EntityId.Should().Be(initial.Id.ToString());
        auditEvent.Operation.Should().Be("insert");
        auditEvent.EventType.Should().Be("submission.created");
        auditEvent.DeletedReason.Should().BeNull();
        auditEvent.Actor.Should().Be("service:waste-obligations");
        auditEvent.Version.Should().Be(1);
        auditEvent.SchemaVersion.Should().Be(ComplianceDeclaration.SchemaVersionValue);
        auditEvent.TraceId.Should().Be(TraceId);
        auditEvent.Before.Should().BeNull();
        auditEvent.After.Should().NotBeNull();
        auditEvent.After["_id"].Should().Be(initial.Id);
        auditEvent.After["version"].Should().Be(1);
        ComplianceDeclarationMetrics.Received(1).Created();
    }

    [Fact]
    public async Task DeclarationMutation_ShouldOnlyUpdateActiveEligibilityGenerationVisibility()
    {
        const string activeGeneration = "active-generation";
        const string retainedGeneration = "retained-generation";
        var declaration = ComplianceDeclarationFixture
            .DirectProducer()
            .With(x => x.Status, ComplianceDeclarationStatus.Cancelled)
            .Create();
        var activeEligibility = OrganisationComplianceDeclarationEligibilityFixture
            .Default(declaration.Organisation.Id)
            .With(x => x.Generation, activeGeneration)
            .With(x => x.ObligationYear, declaration.ObligationYear)
            .With(x => x.RegistrationType, declaration.Organisation.RegistrationType)
            .With(x => x.IsVisibleInUnsubmittedView, false)
            .Create();
        var retainedEligibility = OrganisationComplianceDeclarationEligibilityFixture
            .Default(declaration.Organisation.Id)
            .With(x => x.Generation, retainedGeneration)
            .With(x => x.ObligationYear, declaration.ObligationYear)
            .With(x => x.RegistrationType, declaration.Organisation.RegistrationType)
            .With(x => x.IsVisibleInUnsubmittedView, false)
            .Create();
        await OrganisationEligibilitySnapshots.InsertOneAsync(
            new OrganisationEligibilitySnapshot
            {
                Id = OrganisationEligibilitySnapshot.SnapshotId,
                ActiveGeneration = activeGeneration,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        await OrganisationComplianceDeclarationEligibilities.InsertManyAsync(
            [activeEligibility, retainedEligibility],
            cancellationToken: TestContext.Current.CancellationToken
        );

        var created = await Subject.Create(declaration, TestContext.Current.CancellationToken);

        (await FindEligibility(created, activeGeneration)).IsVisibleInUnsubmittedView.Should().BeTrue();

        var submitted = await Subject.Update(
            created,
            created with
            {
                Status = ComplianceDeclarationStatus.Submitted,
            },
            TestContext.Current.CancellationToken
        );

        (await FindEligibility(submitted, activeGeneration)).IsVisibleInUnsubmittedView.Should().BeFalse();

        var cancelled = await Subject.Update(
            submitted,
            submitted with
            {
                Status = ComplianceDeclarationStatus.Cancelled,
            },
            TestContext.Current.CancellationToken
        );

        (await FindEligibility(cancelled, activeGeneration)).IsVisibleInUnsubmittedView.Should().BeTrue();
        (await FindEligibility(cancelled, retainedGeneration)).Should().BeEquivalentTo(retainedEligibility);
        var snapshot = await OrganisationEligibilitySnapshots
            .Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleAsync(TestContext.Current.CancellationToken);

        snapshot.MaterialisedStateVersion.Should().Be(3);
    }

    [Fact]
    public async Task DeclarationMutation_WhenNoActiveEligibilityGeneration_ShouldIncrementMaterialisedStateVersion()
    {
        var declaration = ComplianceDeclarationFixture
            .DirectProducer()
            .With(x => x.Status, ComplianceDeclarationStatus.Cancelled)
            .Create();
        await OrganisationComplianceDeclarationEligibilities.InsertOneAsync(
            OrganisationComplianceDeclarationEligibilityFixture
                .Default(declaration.Organisation.Id)
                .With(x => x.ObligationYear, declaration.ObligationYear)
                .With(x => x.RegistrationType, declaration.Organisation.RegistrationType)
                .With(x => x.IsVisibleInUnsubmittedView, false)
                .Create(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        await Subject.Create(declaration, TestContext.Current.CancellationToken);

        var snapshot = await OrganisationEligibilitySnapshots
            .Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleAsync(TestContext.Current.CancellationToken);

        snapshot.ActiveGeneration.Should().BeNull();
        snapshot.MaterialisedStateVersion.Should().Be(1);
    }

    [Fact]
    public async Task Create_WhenInserted_WithAudit_ShouldBeValidAudit()
    {
        var initial = await Subject.Create(
            ComplianceDeclarationFixture
                .Default()
                .With(x => x.Audit, AuditEntryFixture.SubmittedThenCancelled())
                .Create(),
            TestContext.Current.CancellationToken
        );

        var retrieved = await Subject.Read(initial.Id.ToString(), TestContext.Current.CancellationToken);

        retrieved.Should().NotBeNull();
        retrieved.Should().BeEquivalentTo(initial);
    }

    [Fact]
    public async Task Create_WhenConcurrent_ShouldCreateEachDeclarationAndAuditEvent()
    {
        const int declarationCount = 40;
        var createTasks = Enumerable
            .Range(0, declarationCount)
            .Select(_ =>
                Subject.Create(ComplianceDeclarationFixture.Default().Create(), TestContext.Current.CancellationToken)
            );

        var declarations = await Task.WhenAll(createTasks);
        var auditEvents = await AuditEvents
            .Find(FilterDefinition<AuditEvent>.Empty)
            .ToListAsync(TestContext.Current.CancellationToken);

        declarations.Should().HaveCount(declarationCount);
        auditEvents.Should().HaveCount(declarationCount);
        auditEvents
            .Select(x => x.Sequence)
            .Should()
            .BeEquivalentTo(Enumerable.Range(1, declarationCount).Select(x => (long)x));
    }

    [Fact]
    public async Task Create_WhenAuditEventFails_ShouldAbortTransaction()
    {
        var database = GetMongoApplicationDatabase();
        var complianceDeclarationMetrics = Substitute.For<IComplianceDeclarationMetrics>();
        var dbContext = CreateDbContext(database);
        var subject = new ComplianceDeclarationService(
            dbContext,
            Substitute.For<ILogger<ComplianceDeclarationService>>(),
            TimeProvider.System,
            new ThrowingAuditEventService(),
            complianceDeclarationMetrics,
            TraceIdReader(),
            new UnsubmittedEligibilityVisibilityService(dbContext)
        );
        var complianceDeclaration = ComplianceDeclarationFixture.Default().Create();
        var act = async () => await subject.Create(complianceDeclaration, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(ThrowingAuditEventService.Message);

        var retrieved = await Subject.Read(complianceDeclaration.Id.ToString(), TestContext.Current.CancellationToken);
        retrieved.Should().BeNull();
        complianceDeclarationMetrics.DidNotReceive().Created();
    }

    [Fact]
    public async Task Read_WhenMatchingData_ShouldReturn()
    {
        var organisationId = Guid.NewGuid();
        const int obligationYear = 2025;

        var result = await Subject.Create(
            ComplianceDeclarationFixture
                .Default()
                .With(x => x.Organisation, OrganisationFixture.Organisation().With(x => x.Id, organisationId).Create())
                .With(x => x.ObligationYear, obligationYear)
                .Create(),
            TestContext.Current.CancellationToken
        );
        await Subject.Create(
            ComplianceDeclarationFixture
                .Default()
                .With(x => x.Organisation, OrganisationFixture.Organisation().With(x => x.Id, organisationId).Create())
                .With(x => x.ObligationYear, obligationYear + 1)
                .Create(),
            TestContext.Current.CancellationToken
        );
        await Subject.Create(
            ComplianceDeclarationFixture.Default().With(x => x.ObligationYear, obligationYear).Create(),
            TestContext.Current.CancellationToken
        );

        var readResult = await Subject.Read(
            organisationId,
            obligationYear,
            page: 1,
            pageSize: 10,
            cancellationToken: TestContext.Current.CancellationToken
        );

        readResult.ComplianceDeclarations.Should().ContainSingle();
        readResult.ComplianceDeclarations.Should().Contain(x => x.Id == result.Id);
        readResult.Total.Should().Be(1);
    }

    [Fact]
    public async Task Read_WhenPaging_ShouldReturnCorrectPageAndTotalInUpdatedOrder()
    {
        var organisationId = Guid.NewGuid();
        const int obligationYear = 2025;
        const int pageSize = 2;
        var oldestId = ObjectId.GenerateNewId();
        var middleId = ObjectId.GenerateNewId();
        var newestId = ObjectId.GenerateNewId();
        var otherDeclarationId = ObjectId.GenerateNewId();
        var organisation = OrganisationFixture.Organisation().With(x => x.Id, organisationId).Create();

        await ComplianceDeclarations.InsertManyAsync(
            [
                ComplianceDeclarationFixture
                    .Default()
                    .With(x => x.Id, oldestId)
                    .With(x => x.Organisation, organisation)
                    .With(x => x.ObligationYear, obligationYear)
                    .With(x => x.Updated, new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc))
                    .Create(),
                ComplianceDeclarationFixture
                    .Default()
                    .With(x => x.Id, middleId)
                    .With(x => x.Organisation, organisation)
                    .With(x => x.ObligationYear, obligationYear)
                    .With(x => x.Updated, new DateTime(2025, 1, 2, 12, 0, 0, DateTimeKind.Utc))
                    .Create(),
                ComplianceDeclarationFixture
                    .Default()
                    .With(x => x.Id, newestId)
                    .With(x => x.Organisation, organisation)
                    .With(x => x.ObligationYear, obligationYear)
                    .With(x => x.Updated, new DateTime(2025, 1, 3, 12, 0, 0, DateTimeKind.Utc))
                    .Create(),
                ComplianceDeclarationFixture
                    .Default()
                    .With(x => x.Id, otherDeclarationId)
                    .With(x => x.ObligationYear, obligationYear)
                    .Create(),
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );

        var firstPage = await Subject.Read(
            organisationId,
            obligationYear,
            page: 1,
            pageSize: pageSize,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var secondPage = await Subject.Read(
            organisationId,
            obligationYear,
            page: 2,
            pageSize: pageSize,
            cancellationToken: TestContext.Current.CancellationToken
        );

        firstPage.ComplianceDeclarations.Select(x => x.Id).Should().Equal(newestId, middleId);
        firstPage.Total.Should().Be(3);
        secondPage.ComplianceDeclarations.Select(x => x.Id).Should().Equal(oldestId);
        secondPage.Total.Should().Be(3);
    }

    [Fact]
    public async Task Delete_WhenNoComplianceDeclaration_ShouldReturnFalse()
    {
        var deleted = await Subject.Delete(ObjectId.GenerateNewId().ToString(), TestContext.Current.CancellationToken);

        deleted.Should().BeFalse();
        ComplianceDeclarationMetrics.DidNotReceive().Deleted();
    }

    [Fact]
    public async Task Delete_WhenDeleted_ShouldRemove()
    {
        var initial = await Subject.Create(
            ComplianceDeclarationFixture.DirectProducer().Create(),
            TestContext.Current.CancellationToken
        );

        var deleted = await Subject.Delete(initial.Id.ToString(), TestContext.Current.CancellationToken);
        var retrieved = await Subject.Read(initial.Id.ToString(), TestContext.Current.CancellationToken);

        deleted.Should().BeTrue();
        retrieved.Should().BeNull();

        var auditEvents = await AuditEvents
            .Find(FilterDefinition<AuditEvent>.Empty)
            .SortBy(x => x.Sequence)
            .ToListAsync(TestContext.Current.CancellationToken);

        auditEvents.Should().HaveCount(2);
        auditEvents[1].Sequence.Should().Be(2);
        auditEvents[1].EntityId.Should().Be(initial.Id.ToString());
        auditEvents[1].Operation.Should().Be("delete");
        auditEvents[1].EventType.Should().Be("submission.removed");
        auditEvents[1].DeletedReason.Should().Be("elevated system allowed removal");
        auditEvents[1].Version.Should().Be(2);
        auditEvents[1].TraceId.Should().Be(TraceId);
        auditEvents[1].Before.Should().NotBeNull();
        auditEvents[1].After.Should().BeNull();
        ComplianceDeclarationMetrics.Received(1).Deleted();
    }

    [Fact]
    public async Task Delete_WhenDatabaseReadPreferenceIsSecondaryPreferred_ShouldRemove()
    {
        var subject = CreateSubject(
            GetMongoApplicationDatabase().WithReadPreference(ReadPreference.SecondaryPreferred)
        );
        var initial = await subject.Create(
            ComplianceDeclarationFixture.DirectProducer().Create(),
            TestContext.Current.CancellationToken
        );

        var deleted = await subject.Delete(initial.Id.ToString(), TestContext.Current.CancellationToken);

        deleted.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_WhenConcurrent_ShouldDeleteEachDeclarationAndAuditEvent()
    {
        const int declarationCount = 40;
        var declarations = await Task.WhenAll(
            Enumerable
                .Range(0, declarationCount)
                .Select(_ =>
                    Subject.Create(
                        ComplianceDeclarationFixture.DirectProducer().Create(),
                        TestContext.Current.CancellationToken
                    )
                )
        );

        var deleted = await Task.WhenAll(
            declarations.Select(x => Subject.Delete(x.Id.ToString(), TestContext.Current.CancellationToken))
        );
        var remainingDeclarations = await ComplianceDeclarations
            .Find(FilterDefinition<ComplianceDeclaration>.Empty)
            .ToListAsync(TestContext.Current.CancellationToken);
        var auditEvents = await AuditEvents
            .Find(FilterDefinition<AuditEvent>.Empty)
            .ToListAsync(TestContext.Current.CancellationToken);

        deleted.Should().OnlyContain(x => x);
        remainingDeclarations.Should().BeEmpty();
        auditEvents.Should().HaveCount(declarationCount * 2);
    }

    [Fact]
    public async Task Delete_WhenAuditEventFails_ShouldAbortTransaction()
    {
        var database = GetMongoApplicationDatabase();
        var complianceDeclarationMetrics = Substitute.For<IComplianceDeclarationMetrics>();
        var dbContext = CreateDbContext(database);
        var subject = new ComplianceDeclarationService(
            dbContext,
            Substitute.For<ILogger<ComplianceDeclarationService>>(),
            TimeProvider.System,
            new ThrowingAuditEventService(),
            complianceDeclarationMetrics,
            TraceIdReader(),
            new UnsubmittedEligibilityVisibilityService(dbContext)
        );
        var initial = await Subject.Create(
            ComplianceDeclarationFixture.DirectProducer().Create(),
            TestContext.Current.CancellationToken
        );
        var act = async () => await subject.Delete(initial.Id.ToString(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(ThrowingAuditEventService.Message);

        var retrieved = await Subject.Read(initial.Id.ToString(), TestContext.Current.CancellationToken);
        retrieved.Should().BeEquivalentTo(initial);
        complianceDeclarationMetrics.DidNotReceive().Deleted();
    }

    [Fact]
    public async Task Update_WhenUpdated_ShouldChange()
    {
        var initial = await Subject.Create(
            ComplianceDeclarationFixture.DirectProducer().Create(),
            TestContext.Current.CancellationToken
        );
        initial.Version.Should().Be(1);
        initial.Created.Should().Be(initial.Updated).And.NotBe(DateTime.MinValue);

        var retrieved = await Subject.Read(initial.Id.ToString(), TestContext.Current.CancellationToken);

        retrieved.Should().NotBeNull();
        var updated = retrieved with { ObligationYear = 2027 };

        retrieved = await Subject.Update(retrieved, updated, TestContext.Current.CancellationToken);
        retrieved.Version.Should().Be(2);
        retrieved.Updated.Should().BeAfter(retrieved.Created);

        retrieved.ObligationYear.Should().Be(2027);

        retrieved = await Subject.Read(initial.Id.ToString(), TestContext.Current.CancellationToken);

        retrieved.Should().NotBeNull();
        retrieved.ObligationYear.Should().Be(2027);

        var auditEvents = await AuditEvents
            .Find(FilterDefinition<AuditEvent>.Empty)
            .SortBy(x => x.Sequence)
            .ToListAsync(TestContext.Current.CancellationToken);

        auditEvents.Should().HaveCount(2);
        auditEvents[1].Sequence.Should().Be(2);
        auditEvents[1].EntityId.Should().Be(initial.Id.ToString());
        auditEvents[1].Operation.Should().Be("update");
        auditEvents[1].EventType.Should().Be("submission.amended");
        auditEvents[1].DeletedReason.Should().BeNull();
        auditEvents[1].Version.Should().Be(2);
        auditEvents[1].TraceId.Should().Be(TraceId);
        auditEvents[1].Before.Should().NotBeNull();
        auditEvents[1].Before!["version"].Should().Be(1);
        auditEvents[1].After.Should().NotBeNull();
        auditEvents[1].After!["version"].Should().Be(2);
        auditEvents[1].After!["obligationYear"].Should().Be(2027);
        ComplianceDeclarationMetrics.Received(1).Updated(retrieved.Status);
    }

    [Fact]
    public async Task Update_WhenConcurrent_SecondShouldFail()
    {
        var initial = await Subject.Create(
            ComplianceDeclarationFixture.DirectProducer().Create(),
            TestContext.Current.CancellationToken
        );

        var retrieved1 = await Subject.Read(initial.Id.ToString(), TestContext.Current.CancellationToken);
        var retrieved2 = await Subject.Read(initial.Id.ToString(), TestContext.Current.CancellationToken);

        retrieved1.Should().NotBeNull();
        retrieved2.Should().NotBeNull();

        var updated1 = retrieved1 with { ObligationYear = 2027 };
        await Subject.Update(retrieved1, updated1, TestContext.Current.CancellationToken);

        var updated2 = retrieved2 with { ObligationYear = 2028 };
        var act = async () => await Subject.Update(retrieved2, updated2, TestContext.Current.CancellationToken);

        await act.Should()
            .ThrowAsync<ConcurrencyException>()
            .WithMessage($"Concurrency issue on write, compliance declaration with id '{initial.Id}' was not updated");

        var auditEvents = await AuditEvents
            .Find(FilterDefinition<AuditEvent>.Empty)
            .SortBy(x => x.Sequence)
            .ToListAsync(TestContext.Current.CancellationToken);

        await Verify(ToVerifyAuditEvents(auditEvents)).ScrubMembers("EntityId", "_id");
    }

    [Fact]
    public async Task Write_WhenMultipleDeclarations_ShouldUseGlobalSequenceAndPerEntityVersion()
    {
        var first = await Subject.Create(
            ComplianceDeclarationFixture.DirectProducer().Create(),
            TestContext.Current.CancellationToken
        );
        var second = await Subject.Create(
            ComplianceDeclarationFixture.DirectProducer().Create(),
            TestContext.Current.CancellationToken
        );

        await Subject.Update(first, first with { ObligationYear = 2027 }, TestContext.Current.CancellationToken);
        await Subject.Update(second, second with { ObligationYear = 2028 }, TestContext.Current.CancellationToken);

        var auditEvents = await AuditEvents
            .Find(FilterDefinition<AuditEvent>.Empty)
            .SortBy(x => x.Sequence)
            .ToListAsync(TestContext.Current.CancellationToken);

        await Verify(ToVerifyAuditEvents(auditEvents)).ScrubMembers("EntityId", "_id").DisableDateCounting();
    }

    [Fact]
    public async Task Search_WhenFilteringByObligationYear_ShouldReturnMatchingResults()
    {
        const int targetYear = 2025;
        const int otherYear = 2026;

        await Subject.Create(
            ComplianceDeclarationFixture.Default().With(x => x.ObligationYear, targetYear).Create(),
            TestContext.Current.CancellationToken
        );
        await Subject.Create(
            ComplianceDeclarationFixture.Default().With(x => x.ObligationYear, targetYear).Create(),
            TestContext.Current.CancellationToken
        );
        await Subject.Create(
            ComplianceDeclarationFixture.Default().With(x => x.ObligationYear, otherYear).Create(),
            TestContext.Current.CancellationToken
        );

        var result = await Search(
            new ComplianceDeclarationSearchQuery { ObligationYear = targetYear },
            1,
            10,
            TestContext.Current.CancellationToken
        );

        result.ComplianceDeclarations.Should().HaveCount(2);
        result.Total.Should().Be(2);
        result.ComplianceDeclarations.Should().AllSatisfy(x => x.ObligationYear.Should().Be(targetYear));
    }

    [Fact]
    public async Task Search_WhenFilteringByStatus_ShouldReturnMatchingResults()
    {
        await Subject.Create(
            ComplianceDeclarationFixture.Default().With(x => x.Status, ComplianceDeclarationStatus.Submitted).Create(),
            TestContext.Current.CancellationToken
        );
        await Subject.Create(
            ComplianceDeclarationFixture.Default().With(x => x.Status, ComplianceDeclarationStatus.Cancelled).Create(),
            TestContext.Current.CancellationToken
        );
        await Subject.Create(
            ComplianceDeclarationFixture.Default().With(x => x.Status, ComplianceDeclarationStatus.Accepted).Create(),
            TestContext.Current.CancellationToken
        );

        var result = await Search(
            new ComplianceDeclarationSearchQuery
            {
                Status = [ComplianceDeclarationStatus.Submitted, ComplianceDeclarationStatus.Cancelled],
            },
            1,
            10,
            TestContext.Current.CancellationToken
        );

        result.ComplianceDeclarations.Should().HaveCount(2);
        result.Total.Should().Be(2);
        result
            .ComplianceDeclarations.Should()
            .AllSatisfy(x =>
                x.Status.Should().BeOneOf(ComplianceDeclarationStatus.Submitted, ComplianceDeclarationStatus.Cancelled)
            );
    }

    [Theory]
    [InlineData(new[] { RegistrationType.DirectProducer })]
    [InlineData(new[] { RegistrationType.ComplianceScheme })]
    [InlineData(new[] { RegistrationType.DirectProducer, RegistrationType.ComplianceScheme })]
    public async Task Search_WhenFilteringByRegistrationType_ShouldReturnMatchingResults(
        RegistrationType[] registrationTypes
    )
    {
        await Subject.Create(
            ComplianceDeclarationFixture.DirectProducer().Create(),
            TestContext.Current.CancellationToken
        );
        await Subject.Create(
            ComplianceDeclarationFixture.ComplianceScheme().Create(),
            TestContext.Current.CancellationToken
        );

        var result = await Search(
            new ComplianceDeclarationSearchQuery { RegistrationType = registrationTypes },
            1,
            10,
            TestContext.Current.CancellationToken
        );

        result.ComplianceDeclarations.Should().HaveCount(registrationTypes.Length);
        result.Total.Should().Be(registrationTypes.Length);
        result
            .ComplianceDeclarations.Should()
            .AllSatisfy(x => x.Organisation.RegistrationType.Should().BeOneOf(registrationTypes));
    }

    [Fact]
    public async Task Search_WhenFilteringByBusinessCountry_ShouldReturnMatchingResults()
    {
        const string businessCountry = "GB-WLS";

        await Subject.Create(
            ComplianceDeclarationFixture
                .Default()
                .With(
                    x => x.Organisation,
                    OrganisationFixture.DirectProducer().With(x => x.BusinessCountry, businessCountry).Create()
                )
                .Create(),
            TestContext.Current.CancellationToken
        );
        await Subject.Create(
            ComplianceDeclarationFixture
                .Default()
                .With(
                    x => x.Organisation,
                    OrganisationFixture.DirectProducer().With(x => x.BusinessCountry, "GB-ENG").Create()
                )
                .Create(),
            TestContext.Current.CancellationToken
        );

        var result = await Subject.Search(
            new ComplianceDeclarationSearchQuery { BusinessCountry = businessCountry },
            1,
            10,
            TestContext.Current.CancellationToken
        );

        result.ComplianceDeclarations.Should().ContainSingle();
        result.Total.Should().Be(1);
        result.ComplianceDeclarations.Single().Organisation.BusinessCountry.Should().Be(businessCountry);
    }

    [Theory]
    [InlineData("zeina foods")] // organisation name
    [InlineData("ZEINA")] // partial name
    [InlineData("zeina foods limited")] // full name, lowercased against an uppercase stored value
    [InlineData("green scheme")] // compliance scheme name
    [InlineData("operator co")] // scheme operator name, which is what the UI displays for schemes
    [InlineData("OPERATOR")] // partial operator name in a different case
    [InlineData(MatchingReferenceNumber)] // reference number
    [InlineData("0024")] // partial reference number
    public async Task Search_WhenFiltering_ShouldMatchAnyOrganisationField(string term)
    {
        await CreateMatchingAndNonMatchingDeclarations();

        var result = await Search(
            new ComplianceDeclarationSearchQuery { Search = term },
            TestContext.Current.CancellationToken
        );

        result.ComplianceDeclarations.Should().ContainSingle();
        result.ComplianceDeclarations.Single().Organisation.ReferenceNumber.Should().Be(MatchingReferenceNumber);
    }

    [Theory]
    [InlineData("zzzznomatchzzzz")]
    [InlineData("9999999")]
    public async Task Search_WhenNothingMatches_ShouldReturnNoResults(string term)
    {
        await CreateMatchingAndNonMatchingDeclarations();

        var result = await Search(
            new ComplianceDeclarationSearchQuery { Search = term },
            TestContext.Current.CancellationToken
        );

        result.ComplianceDeclarations.Should().BeEmpty();
        result.Total.Should().Be(0);
    }

    [Fact]
    public async Task Search_WhenTermIsSurroundedByWhitespace_ShouldBeTrimmed()
    {
        await CreateMatchingAndNonMatchingDeclarations();

        var result = await Search(
            new ComplianceDeclarationSearchQuery { Search = "  zeina  " },
            TestContext.Current.CancellationToken
        );

        result.ComplianceDeclarations.Should().ContainSingle();
    }

    [Fact]
    public async Task Search_WhenTermMatchesSeveralDeclarations_ShouldReturnAllOfThem()
    {
        const string sharedName = "Repeat Submitter Ltd";
        var organisation = OrganisationFixture.Organisation().With(x => x.Name, sharedName).Create();
        await CreateDeclarationForOrganisation(organisation);
        await CreateDeclarationForOrganisation(organisation);

        var result = await Search(
            new ComplianceDeclarationSearchQuery { Search = "repeat submitter" },
            TestContext.Current.CancellationToken
        );

        result.ComplianceDeclarations.Should().HaveCount(2);
        result.Total.Should().Be(2);
    }

    [Fact]
    public async Task Search_WhenTermContainsRegexMetacharacters_ShouldTreatThemLiterally()
    {
        await CreateDeclarationForOrganisation(
            OrganisationFixture.Organisation().With(x => x.Name, "AxB Trading").Create()
        );

        var result = await Search(
            new ComplianceDeclarationSearchQuery { Search = "A.B" },
            TestContext.Current.CancellationToken
        );

        result.ComplianceDeclarations.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_WhenCombinedWithStatus_ShouldApplyBoth()
    {
        var organisation = OrganisationFixture.Organisation().With(x => x.Name, "Combined Filters Ltd").Create();
        var submitted = await CreateDeclarationForOrganisation(organisation);
        await Subject.Update(
            submitted,
            submitted with
            {
                Status = ComplianceDeclarationStatus.Accepted,
            },
            TestContext.Current.CancellationToken
        );
        await CreateDeclarationForOrganisation(organisation);

        var result = await Search(
            new ComplianceDeclarationSearchQuery
            {
                Search = "combined filters",
                Status = [ComplianceDeclarationStatus.Accepted],
            },
            TestContext.Current.CancellationToken
        );

        result.ComplianceDeclarations.Should().ContainSingle();
        result.ComplianceDeclarations.Single().Status.Should().Be(ComplianceDeclarationStatus.Accepted);
    }

    [Fact]
    public async Task Search_WhenCombinedWithRegistrationType_ShouldApplyBoth()
    {
        const string sharedName = "Dual Type Ltd";
        await CreateDeclarationForOrganisation(
            OrganisationFixture
                .Organisation()
                .With(x => x.Name, sharedName)
                .With(x => x.RegistrationType, RegistrationType.DirectProducer)
                .Create()
        );
        await CreateDeclarationForOrganisation(
            OrganisationFixture
                .Organisation()
                .With(x => x.Name, sharedName)
                .With(x => x.RegistrationType, RegistrationType.ComplianceScheme)
                .Create()
        );

        var result = await Search(
            new ComplianceDeclarationSearchQuery
            {
                Search = "dual type",
                RegistrationType = [RegistrationType.ComplianceScheme],
            },
            TestContext.Current.CancellationToken
        );

        result.ComplianceDeclarations.Should().ContainSingle();
        result
            .ComplianceDeclarations.Single()
            .Organisation.RegistrationType.Should()
            .Be(RegistrationType.ComplianceScheme);
    }

    [Fact]
    public async Task Search_WhenCombinedWithObligationYear_ShouldApplyBoth()
    {
        const string sharedName = "Two Year Ltd";
        const int matchingYear = 2026;
        var organisation = OrganisationFixture.Organisation().With(x => x.Name, sharedName).Create();
        await Subject.Create(
            ComplianceDeclarationFixture
                .Default()
                .With(x => x.Organisation, organisation)
                .With(x => x.ObligationYear, matchingYear)
                .Create(),
            TestContext.Current.CancellationToken
        );
        await Subject.Create(
            ComplianceDeclarationFixture
                .Default()
                .With(x => x.Organisation, organisation)
                .With(x => x.ObligationYear, 2027)
                .Create(),
            TestContext.Current.CancellationToken
        );

        var result = await Search(
            new ComplianceDeclarationSearchQuery { Search = "two year", ObligationYear = matchingYear },
            TestContext.Current.CancellationToken
        );

        result.ComplianceDeclarations.Should().ContainSingle();
        result.ComplianceDeclarations.Single().ObligationYear.Should().Be(matchingYear);
    }

    private async Task CreateMatchingAndNonMatchingDeclarations()
    {
        await CreateDeclarationForOrganisation(
            OrganisationFixture
                .Organisation()
                .With(x => x.Name, "ZEINA FOODS LIMITED")
                .With(x => x.ComplianceSchemeName, "Green Scheme")
                .With(x => x.SchemeOperatorName, "Operator Co")
                .With(x => x.ReferenceNumber, MatchingReferenceNumber)
                .Create()
        );
        await CreateDeclarationForOrganisation(
            OrganisationFixture
                .Organisation()
                .With(x => x.Name, "Unrelated Holdings")
                .With(x => x.ComplianceSchemeName, (string?)null)
                .With(x => x.SchemeOperatorName, (string?)null)
                .With(x => x.ReferenceNumber, "999999")
                .Create()
        );
    }

    private Task<ComplianceDeclaration> CreateDeclarationForOrganisation(Organisation organisation) =>
        Subject.Create(
            ComplianceDeclarationFixture.Default().With(x => x.Organisation, organisation).Create(),
            TestContext.Current.CancellationToken
        );

    private Task<ComplianceDeclarationPageResult> Search(
        ComplianceDeclarationSearchQuery query,
        CancellationToken cancellationToken
    ) => Search(query, 1, 10, cancellationToken);

    private Task<ComplianceDeclarationPageResult> Search(
        ComplianceDeclarationSearchQuery query,
        int page,
        int pageSize,
        CancellationToken cancellationToken
    )
    {
        AllowUnindexedSearchQueries(query);

        return Subject.Search(query, page, pageSize, cancellationToken);
    }

    private void AllowUnindexedSearchQueries(ComplianceDeclarationSearchQuery query)
    {
        if (query.ObligationYear.HasValue)
            return;

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            AllowUnindexedSearchCount(
                SearchFilterShape(query),
                "Case-insensitive contains search over four alternative organisation fields has no viable B-tree index."
            );

            return;
        }

        if (query.Status is { Length: > 0 })
        {
            AllowUnindexedSearchCount(
                "{status:{$in:[?]}}",
                "Status-only filtering is low-selectivity; the existing search index deliberately begins with obligationYear."
            );

            return;
        }

        if (query.RegistrationType is { Length: > 0 })
        {
            AllowUnindexedSearchCount(
                "{organisation.registrationType:{$in:[?]}}",
                "Registration-type-only filtering is low-selectivity; the existing search index deliberately begins with obligationYear."
            );

            return;
        }

        AllowUnindexedSearchCount("{}", "An exact count with no filter must inspect every compliance declaration.");

        if (query.Sort is { Length: > 0 })
        {
            AllowUnindexedMongoQuery(
                new MongoQueryProfileAllowance(
                    "query",
                    ComplianceDeclarationNamespace,
                    "{}",
                    "MO-485",
                    "Unbounded custom sorting would require an index for every sortable field and direction."
                )
            );
        }
    }

    private static string SearchFilterShape(ComplianceDeclarationSearchQuery query)
    {
        var filters = new List<string> { OrganisationSearchFilter };

        if (query.RegistrationType is { Length: > 0 })
            filters.Add("organisation.registrationType:{$in:[?]}");

        if (query.Status is { Length: > 0 })
            filters.Add("status:{$in:[?]}");

        return $"{{{string.Join(",", filters)}}}";
    }

    private void AllowUnindexedSearchCount(string filterShape, string reason) =>
        AllowUnindexedMongoQuery(
            new MongoQueryProfileAllowance("command", ComplianceDeclarationNamespace, filterShape, "MO-485", reason)
        );

    [Fact]
    public async Task Search_WhenPaging_ShouldReturnCorrectPageAndTotal()
    {
        const int pageSize = 2;
        for (var i = 0; i < 5; i++)
        {
            await Subject.Create(
                ComplianceDeclarationFixture.Default().Create(),
                TestContext.Current.CancellationToken
            );
        }

        var page1 = await Search(
            new ComplianceDeclarationSearchQuery(),
            1,
            pageSize,
            TestContext.Current.CancellationToken
        );
        var page2 = await Search(
            new ComplianceDeclarationSearchQuery(),
            2,
            pageSize,
            TestContext.Current.CancellationToken
        );
        var page3 = await Search(
            new ComplianceDeclarationSearchQuery(),
            3,
            pageSize,
            TestContext.Current.CancellationToken
        );

        page1.ComplianceDeclarations.Should().HaveCount(pageSize);
        page1.Total.Should().Be(5);

        page2.ComplianceDeclarations.Should().HaveCount(pageSize);
        page2.Total.Should().Be(5);

        page3.ComplianceDeclarations.Should().HaveCount(1);
        page3.Total.Should().Be(5);
    }

    [Fact]
    public async Task Search_WhenPageOutOfBounds_ShouldReturnEmptyWithCorrectTotal()
    {
        await Subject.Create(ComplianceDeclarationFixture.Default().Create(), TestContext.Current.CancellationToken);

        var result = await Search(
            new ComplianceDeclarationSearchQuery(),
            10,
            10,
            TestContext.Current.CancellationToken
        );

        result.ComplianceDeclarations.Should().BeEmpty();
        result.Total.Should().Be(1);
    }

    [Fact]
    public async Task Search_WhenDeclarationUpdated_ShouldRetainPositionInPagedList()
    {
        const int pageSize = 1;
        var records = new List<ComplianceDeclaration>();

        // Create records in a way that we know their order (by ID)
        // Since we use SortBy(x => x.Id), we need to ensure the ID is the sort key
        for (var i = 0; i < 3; i++)
        {
            records.Add(
                await Subject.Create(
                    ComplianceDeclarationFixture.Default().Create(),
                    TestContext.Current.CancellationToken
                )
            );
        }

        var sortedIds = records.Select(x => x.Id).OrderBy(id => id).ToList();
        var targetRecord = records.First(x => x.Id == sortedIds[1]);

        // Verify initial position (Page 2)
        var search1 = await Search(
            new ComplianceDeclarationSearchQuery(),
            2,
            pageSize,
            TestContext.Current.CancellationToken
        );
        search1.ComplianceDeclarations.First().Id.Should().Be(targetRecord.Id);

        // Update the record
        var updated = await Subject.Update(
            targetRecord,
            targetRecord with
            {
                ObligationYear = 9999,
            },
            TestContext.Current.CancellationToken
        );

        // Verify position is retained (still Page 2)
        var search2 = await Search(
            new ComplianceDeclarationSearchQuery(),
            2,
            pageSize,
            TestContext.Current.CancellationToken
        );
        search2.ComplianceDeclarations.First().Id.Should().Be(targetRecord.Id);
        search2.ComplianceDeclarations.First().ObligationYear.Should().Be(9999);
        updated.Updated.Should().BeAfter(targetRecord.Updated);
        search2.ComplianceDeclarations.First().Updated.Should().Be(updated.Updated);
    }

    [Fact]
    public async Task Search_WhenSearchContainsRegexCharacters_ShouldTreatLiterally()
    {
        const string regexName = "Waste Management Ltd (UK)";
        const string otherName = "Waste Management Ltd";

        await CreateDeclarationForOrganisation(
            OrganisationFixture.Organisation().With(x => x.Name, regexName).Create()
        );
        await CreateDeclarationForOrganisation(
            OrganisationFixture.Organisation().With(x => x.Name, otherName).Create()
        );

        var result = await Search(
            new ComplianceDeclarationSearchQuery { Search = regexName },
            TestContext.Current.CancellationToken
        );

        result.ComplianceDeclarations.Should().ContainSingle();
        result.ComplianceDeclarations.Should().Contain(x => x.Organisation.Name == regexName);
    }

    [Fact]
    public async Task Search_WhenSortingByMultipleFields_ShouldApplyThemInPriorityOrderThenId()
    {
        var firstId = ObjectId.Parse("000000000000000000000001");
        var secondId = ObjectId.Parse("000000000000000000000002");
        var thirdId = ObjectId.Parse("000000000000000000000003");
        var fourthId = ObjectId.Parse("000000000000000000000004");
        var fifthId = ObjectId.Parse("000000000000000000000005");
        var organisationId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var otherOrganisationId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var date = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        await ComplianceDeclarations.InsertManyAsync(
            [
                ComplianceDeclarationFixture
                    .Default()
                    .With(x => x.Id, firstId)
                    .With(
                        x => x.Organisation,
                        OrganisationFixture.DirectProducer(organisationId).With(x => x.Name, "Bravo").Create()
                    )
                    .With(x => x.Created, date)
                    .Create(),
                ComplianceDeclarationFixture
                    .Default()
                    .With(x => x.Id, secondId)
                    .With(
                        x => x.Organisation,
                        OrganisationFixture.DirectProducer(organisationId).With(x => x.Name, "Alpha").Create()
                    )
                    .With(x => x.Created, date)
                    .Create(),
                ComplianceDeclarationFixture
                    .Default()
                    .With(x => x.Id, thirdId)
                    .With(
                        x => x.Organisation,
                        OrganisationFixture.DirectProducer(otherOrganisationId).With(x => x.Name, "Alpha").Create()
                    )
                    .With(x => x.Created, date)
                    .Create(),
                ComplianceDeclarationFixture
                    .Default()
                    .With(x => x.Id, fourthId)
                    .With(
                        x => x.Organisation,
                        OrganisationFixture.DirectProducer(otherOrganisationId).With(x => x.Name, "Charlie").Create()
                    )
                    .With(x => x.Created, date.AddDays(-1))
                    .Create(),
                ComplianceDeclarationFixture
                    .Default()
                    .With(x => x.Id, fifthId)
                    .With(
                        x => x.Organisation,
                        OrganisationFixture.DirectProducer(organisationId).With(x => x.Name, "Alpha").Create()
                    )
                    .With(x => x.Created, date)
                    .Create(),
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );

        var result = await Search(
            new ComplianceDeclarationSearchQuery
            {
                Sort =
                [
                    new ComplianceDeclarationSort
                    {
                        Field = ComplianceDeclarationSortField.DateSubmitted,
                        Direction = ComplianceDeclarationSortDirection.Descending,
                    },
                    new ComplianceDeclarationSort
                    {
                        Field = ComplianceDeclarationSortField.OrganisationName,
                        Direction = ComplianceDeclarationSortDirection.Ascending,
                    },
                    new ComplianceDeclarationSort
                    {
                        Field = ComplianceDeclarationSortField.OrganisationId,
                        Direction = ComplianceDeclarationSortDirection.Ascending,
                    },
                ],
            },
            1,
            10,
            TestContext.Current.CancellationToken
        );

        result.ComplianceDeclarations.Select(x => x.Id).Should().Equal(secondId, fifthId, thirdId, firstId, fourthId);
    }

    [Fact]
    public async Task Search_WhenPrimarySortValuesEqual_ShouldOrderByOrganisationName()
    {
        var bravoId = ObjectId.Parse("000000000000000000000001");
        var alphaId = ObjectId.Parse("000000000000000000000002");
        var date = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        await ComplianceDeclarations.InsertManyAsync(
            [
                ComplianceDeclarationFixture
                    .Default()
                    .With(x => x.Id, bravoId)
                    .With(x => x.Organisation, OrganisationFixture.DirectProducer().With(x => x.Name, "Bravo").Create())
                    .With(x => x.Created, date)
                    .Create(),
                ComplianceDeclarationFixture
                    .Default()
                    .With(x => x.Id, alphaId)
                    .With(x => x.Organisation, OrganisationFixture.DirectProducer().With(x => x.Name, "Alpha").Create())
                    .With(x => x.Created, date)
                    .Create(),
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );

        var result = await Search(
            new ComplianceDeclarationSearchQuery
            {
                Sort =
                [
                    new ComplianceDeclarationSort
                    {
                        Field = ComplianceDeclarationSortField.DateSubmitted,
                        Direction = ComplianceDeclarationSortDirection.Descending,
                    },
                ],
            },
            1,
            10,
            TestContext.Current.CancellationToken
        );

        result.ComplianceDeclarations.Select(x => x.Id).Should().Equal(alphaId, bravoId);
    }

    [Theory]
    [InlineData(
        ComplianceDeclarationSortField.RecyclingObligations,
        ComplianceDeclarationSortDirection.Ascending,
        false
    )]
    [InlineData(ComplianceDeclarationSortField.PercentageMet, ComplianceDeclarationSortDirection.Ascending, false)]
    [InlineData(ComplianceDeclarationSortField.DateSubmitted, ComplianceDeclarationSortDirection.Ascending, false)]
    [InlineData(ComplianceDeclarationSortField.Regulation43, ComplianceDeclarationSortDirection.Ascending, false)]
    [InlineData(ComplianceDeclarationSortField.OrganisationName, ComplianceDeclarationSortDirection.Ascending, false)]
    [InlineData(ComplianceDeclarationSortField.OrganisationId, ComplianceDeclarationSortDirection.Ascending, false)]
    [InlineData(
        ComplianceDeclarationSortField.RecyclingObligations,
        ComplianceDeclarationSortDirection.Descending,
        true
    )]
    [InlineData(ComplianceDeclarationSortField.PercentageMet, ComplianceDeclarationSortDirection.Descending, true)]
    [InlineData(ComplianceDeclarationSortField.DateSubmitted, ComplianceDeclarationSortDirection.Descending, true)]
    [InlineData(ComplianceDeclarationSortField.Regulation43, ComplianceDeclarationSortDirection.Descending, true)]
    [InlineData(ComplianceDeclarationSortField.OrganisationName, ComplianceDeclarationSortDirection.Descending, true)]
    [InlineData(ComplianceDeclarationSortField.OrganisationId, ComplianceDeclarationSortDirection.Descending, true)]
    public async Task Search_WhenSorting_ShouldOrderByTheRequestedField(
        ComplianceDeclarationSortField field,
        ComplianceDeclarationSortDirection direction,
        bool firstDeclarationShouldBeFirst
    )
    {
        var firstId = ObjectId.Parse("000000000000000000000001");
        var secondId = ObjectId.Parse("000000000000000000000002");
        var firstOrganisationId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondOrganisationId = Guid.Parse("00000000-0000-0000-0000-000000000002");

        await ComplianceDeclarations.InsertManyAsync(
            [
                ComplianceDeclarationFixture
                    .Default()
                    .With(x => x.Id, firstId)
                    .With(x => x.ObligationStatus, Defra.WasteObligations.Api.Dtos.ObligationStatus.Met)
                    .With(x => x.ObligationCoveragePercentage, 80m)
                    .With(x => x.Created, new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc))
                    .With(x => x.IsRegulation43Compliant, true)
                    .With(
                        x => x.Organisation,
                        OrganisationFixture.DirectProducer(secondOrganisationId).With(x => x.Name, "Bravo").Create()
                    )
                    .Create(),
                ComplianceDeclarationFixture
                    .Default()
                    .With(x => x.Id, secondId)
                    .With(x => x.ObligationStatus, Defra.WasteObligations.Api.Dtos.ObligationStatus.NotMet)
                    .With(x => x.ObligationCoveragePercentage, 20m)
                    .With(x => x.Created, new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc))
                    .With(x => x.IsRegulation43Compliant, false)
                    .With(
                        x => x.Organisation,
                        OrganisationFixture.DirectProducer(firstOrganisationId).With(x => x.Name, "Alpha").Create()
                    )
                    .Create(),
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );

        var result = await Search(
            new ComplianceDeclarationSearchQuery
            {
                Sort = [new ComplianceDeclarationSort { Field = field, Direction = direction }],
            },
            1,
            10,
            TestContext.Current.CancellationToken
        );

        var expectedFirstId = firstDeclarationShouldBeFirst ? firstId : secondId;
        var expectedSecondId = firstDeclarationShouldBeFirst ? secondId : firstId;

        result.ComplianceDeclarations.Select(x => x.Id).Should().Equal(expectedFirstId, expectedSecondId);
    }

    private static IEnumerable<object> ToVerifyAuditEvents(IEnumerable<AuditEvent> auditEvents) =>
        auditEvents.Select(x => new
        {
            x.EventId,
            x.Sequence,
            x.Entity,
            x.EntityId,
            x.Operation,
            x.EventType,
            x.DeletedReason,
            x.OccurredAt,
            x.RecordedAt,
            x.Actor,
            x.Version,
            Before = ToPlainDocument(x.Before),
            After = ToPlainDocument(x.After),
            x.SchemaVersion,
        });

    private static HeaderPropagationValues HeaderPropagationValues() =>
        new() { Headers = new Dictionary<string, StringValues> { [TraceHeaderName] = TraceId } };

    private static TraceIdReader TraceIdReader() =>
        new(HeaderPropagationValues(), Options.Create(new TraceHeader { Name = TraceHeaderName }));

    private static ComplianceDeclarationService CreateSubject(IMongoDatabase database)
    {
        var dbContext = CreateDbContext(database);

        return new(
            dbContext,
            Substitute.For<ILogger<ComplianceDeclarationService>>(),
            TimeProvider.System,
            new AuditEventService(new AuditEventDbContext(database), TimeProvider.System, new FakeEventIdGenerator()),
            Substitute.For<IComplianceDeclarationMetrics>(),
            TraceIdReader(),
            new UnsubmittedEligibilityVisibilityService(dbContext)
        );
    }

    private static MongoDbContext CreateDbContext(IMongoDatabase database) =>
        new(database, Options.Create(new MongoDbOptions()), Substitute.For<ILogger<MongoDbContext>>());

    private async Task<OrganisationComplianceDeclarationEligibility> FindEligibility(
        ComplianceDeclaration declaration,
        string generation
    ) =>
        await OrganisationComplianceDeclarationEligibilities
            .Find(x =>
                x.Generation == generation
                && x.OrganisationId == declaration.Organisation.Id
                && x.ObligationYear == declaration.ObligationYear
                && x.RegistrationType == declaration.Organisation.RegistrationType
            )
            .SingleAsync(TestContext.Current.CancellationToken);

    private static object? ToPlainDocument(BsonDocument? document)
    {
        return document is null ? null : ToPlainValue(document);
    }

    private static object? ToPlainValue(BsonValue value) =>
        value.BsonType switch
        {
            BsonType.Array => value.AsBsonArray.Select(ToPlainValue).ToList(),
            BsonType.Boolean => value.AsBoolean,
            BsonType.DateTime => value.ToUniversalTime(),
            BsonType.Decimal128 => value.AsDecimal,
            BsonType.Document => value.AsBsonDocument.ToDictionary(x => x.Name, x => ToPlainValue(x.Value)),
            BsonType.Double => value.AsDouble,
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => value.AsInt64,
            BsonType.Null => null,
            BsonType.ObjectId => value.AsObjectId.ToString(),
            BsonType.String => value.AsString,
            _ => BsonTypeMapper.MapToDotNetValue(value),
        };

    private class ThrowingAuditEventService : IAuditEventService
    {
        public const string Message = "Audit event failed";

        public Task RecordEvent(
            IClientSessionHandle session,
            AuditEventRequest auditEvent,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException(Message);
    }
}
