using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Services.ObligationCalculations;
using Defra.WasteObligations.AuditEvents;
using Defra.WasteObligations.AuditEvents.Data;
using Defra.WasteObligations.AuditEvents.Entities;
using Defra.WasteObligations.Testing.Fixtures.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace Defra.WasteObligations.Api.IntegrationTests.Services.ObligationCalculations;

public class ObligationCalculationServiceTests : IntegrationTestBase
{
    [Fact]
    public async Task SaveWithOutcome_ShouldReportCommittedWritesAndEquivalentReplay()
    {
        var subject = Subject();
        var created = await subject.SaveWithOutcome(Calculation(), TestContext.Current.CancellationToken);
        var unchanged = await subject.SaveWithOutcome(created.Calculation, TestContext.Current.CancellationToken);
        var updated = await subject.SaveWithOutcome(
            ChangeKilograms(created.Calculation),
            TestContext.Current.CancellationToken
        );

        created.Outcome.Should().Be(CalculationSaveOutcome.Created);
        unchanged.Outcome.Should().Be(CalculationSaveOutcome.Unchanged);
        updated.Outcome.Should().Be(CalculationSaveOutcome.Updated);
        updated.Calculation.Version.Should().Be(2);
        var events = await Events(created.Calculation.Id);
        events.Should().HaveCount(2);
        events.Select(x => x.Operation).Should().Equal("insert", "update");
        events
            .Select(x => x.EventType)
            .Should()
            .Equal("obligation_calculation.created", "obligation_calculation.updated");
        events.Select(x => x.Version).Should().Equal(1, 2);
        events.Select(x => x.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        events.Select(x => x.EventId).Should().OnlyHaveUniqueItems();
        events
            .Should()
            .AllSatisfy(x =>
            {
                x.EntityId.Should().Be(created.Calculation.Id.ToString());
                x.Actor.Should().Be("system:waste-obligations");
                x.SchemaVersion.Should().Be("v1.0");
                x.DeletedReason.Should().BeNull();
                x.RecordedAt.Should().BeOnOrAfter(x.OccurredAt);
            });
        events[0].Before.Should().BeNull();
        events[0].After.Should().Be(created.Calculation.ToBsonDocument());
        events[1].Before.Should().Be(created.Calculation.ToBsonDocument());
        events[1].After.Should().Be(updated.Calculation.ToBsonDocument());
        events[0].OccurredAt.Should().Be(created.Calculation.CalculatedOn);
        events[1].OccurredAt.Should().Be(updated.Calculation.CalculatedOn);
        (
            await History.CountDocumentsAsync(
                FilterDefinition<ObligationCalculationHistory>.Empty,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(1);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, 1)]
    public async Task Save_ShouldRecordNoEarlierThanCalculationCompletion(int clockOffset, int expectedOffset)
    {
        var calculation = Calculation();
        var clock = new FakeTimeProvider(new DateTimeOffset(calculation.CalculatedOn).AddMilliseconds(clockOffset));

        var saved = await Subject(timeProvider: clock).Save(calculation, TestContext.Current.CancellationToken);

        var auditEvent = (await Events(saved.Id)).Should().ContainSingle().Which;
        auditEvent.OccurredAt.Should().Be(calculation.CalculatedOn);
        auditEvent.RecordedAt.Should().Be(calculation.CalculatedOn.AddMilliseconds(expectedOffset));
    }

    [Fact]
    public async Task Save_ShouldPersistWholeCalculationAndKeepEquivalentDataUnchanged()
    {
        var input = Calculation();
        var subject = Subject();

        var saved = await subject.Save(input, TestContext.Current.CancellationToken);
        var reordered = input with
        {
            CalculatedOn = input.CalculatedOn.AddDays(1),
            Producers = input
                .Producers.Reverse()
                .Select(x => x with { Materials = x.Materials.Reverse().ToArray() })
                .ToArray(),
        };
        var equivalent = await subject.Save(reordered, TestContext.Current.CancellationToken);
        var stored = await Current.Find(x => x.Id == saved.Id).SingleAsync(TestContext.Current.CancellationToken);

        saved.Id.Should().NotBe(ObjectId.Empty);
        stored.Should().BeEquivalentTo(input, options => options.Excluding(x => x.Id));
        stored.Version.Should().Be(1);
        stored.SchemaVersion.Should().Be("v1.0");
        equivalent.Should().BeEquivalentTo(saved);
        (
            await History.CountDocumentsAsync(
                FilterDefinition<ObligationCalculationHistory>.Empty,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(0);
        var bson = await RawCurrent
            .Find(new BsonDocument("_id", saved.Id))
            .SingleAsync(TestContext.Current.CancellationToken);
        bson["submitterOrganisationId"].AsBsonBinaryData.SubType.Should().Be(BsonBinarySubType.UuidStandard);
        bson["submitterType"].AsString.Should().Be("DirectRegistrant");
        bson["producers"]
            [0]["materials"]
            .AsBsonArray.Should()
            .AllSatisfy(x =>
            {
                x["inputKilograms"].BsonType.Should().Be(BsonType.Int64);
                x["inputTonnes"].BsonType.Should().Be(BsonType.Int64);
                x["obligationTonnes"].BsonType.Should().Be(BsonType.Int64);
            });
        (await Events(saved.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Save_WhenIncomingIdentityAndVersionAreStale_ShouldUseCurrentStoredVersion()
    {
        var subject = Subject();
        var first = await subject.Save(Calculation(), TestContext.Current.CancellationToken);
        var second = await subject.Save(ChangeKilograms(first), TestContext.Current.CancellationToken);
        var stale = ChangeKilograms(ChangeKilograms(first)) with { Id = ObjectId.GenerateNewId() };

        var saved = await subject.Save(stale, TestContext.Current.CancellationToken);

        saved.Id.Should().Be(first.Id);
        saved.Version.Should().Be(3);
        (
            await History
                .Find(x => x.Calculation.Id == first.Id)
                .SortBy(x => x.Calculation.Version)
                .ToListAsync(TestContext.Current.CancellationToken)
        )
            .Select(x => x.Calculation.Version)
            .Should()
            .Equal(1, 2);
        saved
            .Producers[0]
            .Materials[0]
            .InputKilograms.Should()
            .Be(second.Producers[0].Materials[0].InputKilograms + 100);
        var auditEvent = (await Events(first.Id)).Last();
        auditEvent.Version.Should().Be(3);
        auditEvent.Before.Should().Be(second.ToBsonDocument());
        auditEvent.After.Should().Be(saved.ToBsonDocument());
    }

    [Fact]
    public async Task Save_ShouldArchiveExactPreviousPayloadAndOnlySupersededVersions()
    {
        var subject = Subject();
        var first = await subject.Save(Calculation(), TestContext.Current.CancellationToken);
        await RawCurrent.UpdateOneAsync(
            new BsonDocument("_id", first.Id),
            new BsonDocument(
                "$set",
                new BsonDocument { { "futureField", new BsonDocument("nested", 42) }, { "schemaVersion", "v0.9" } }
            ),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var original = await RawCurrent
            .Find(new BsonDocument("_id", first.Id))
            .SingleAsync(TestContext.Current.CancellationToken);
        var changed = ChangeKilograms(first);

        var second = await subject.Save(changed, TestContext.Current.CancellationToken);
        var third = await subject.Save(ChangeKilograms(second), TestContext.Current.CancellationToken);
        await subject.Save(
            third with
            {
                CalculatedOn = third.CalculatedOn.AddDays(1),
            },
            TestContext.Current.CancellationToken
        );
        var history = await History
            .Find(x => x.Calculation.Id == first.Id)
            .SortBy(x => x.Calculation.Version)
            .ToListAsync(TestContext.Current.CancellationToken);

        third.Id.Should().Be(first.Id);
        third.Version.Should().Be(3);
        history.Select(x => x.Calculation.Version).Should().Equal(1, 2);
        history[0].Calculation.SchemaVersion.Should().Be("v0.9");
        history[1].Calculation.SchemaVersion.Should().Be("v1.0");
        var rawHistory = await GetMongoDatabase()
            .GetCollection<BsonDocument>(nameof(ObligationCalculationHistory))
            .Find(new BsonDocument("calculation._id", first.Id))
            .Sort(new BsonDocument("calculation.version", 1))
            .ToListAsync(TestContext.Current.CancellationToken);
        rawHistory[0].Contains("calculationId").Should().BeFalse();
        rawHistory[0].Contains("version").Should().BeFalse();
        rawHistory[0]["calculation"].AsBsonDocument.Equals(original).Should().BeTrue();
        rawHistory[1]["calculation"].AsBsonDocument.Equals(second.ToBsonDocument()).Should().BeTrue();
        second.Producers[0].Materials[0].InputTonnes.Should().Be(first.Producers[0].Materials[0].InputTonnes);
        second.Producers[0].Materials[0].ObligationTonnes.Should().Be(first.Producers[0].Materials[0].ObligationTonnes);
        var events = await Events(first.Id);
        events.Should().HaveCount(3);
        events[1].Before.Should().Be(original);
        events[1].After.Should().Be(second.ToBsonDocument());
    }

    [Fact]
    public async Task Save_WhenStoredVersionIsInt64_ShouldIncrementAndArchiveOriginalPayload()
    {
        var subject = Subject();
        var first = await subject.Save(Calculation(), TestContext.Current.CancellationToken);
        await RawCurrent.UpdateOneAsync(
            new BsonDocument("_id", first.Id),
            new BsonDocument("$set", new BsonDocument("version", new BsonInt64(2))),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var original = await RawCurrent
            .Find(new BsonDocument("_id", first.Id))
            .SingleAsync(TestContext.Current.CancellationToken);

        var saved = await subject.Save(ChangeKilograms(first), TestContext.Current.CancellationToken);

        saved.Id.Should().Be(first.Id);
        saved.Version.Should().Be(3);
        (await Current.Find(x => x.Id == first.Id).SingleAsync(TestContext.Current.CancellationToken))
            .Version.Should()
            .Be(3);
        var archived = await GetMongoDatabase()
            .GetCollection<BsonDocument>(nameof(ObligationCalculationHistory))
            .Find(new BsonDocument("calculation._id", first.Id))
            .SingleAsync(TestContext.Current.CancellationToken);
        archived["calculation"]["version"].BsonType.Should().Be(BsonType.Int64);
        archived["calculation"].AsBsonDocument.Equals(original).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_WhenStoredVersionIsInvalid_ShouldNotWrite(bool outOfRange)
    {
        var subject = Subject();
        var first = await subject.Save(Calculation(), TestContext.Current.CancellationToken);
        BsonValue version = outOfRange ? new BsonInt64((long)int.MaxValue + 1) : new BsonDouble(2.5);
        await RawCurrent.UpdateOneAsync(
            new BsonDocument("_id", first.Id),
            new BsonDocument("$set", new BsonDocument("version", version)),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var original = await RawCurrent
            .Find(new BsonDocument("_id", first.Id))
            .SingleAsync(TestContext.Current.CancellationToken);

        Func<Task> save = () => subject.Save(ChangeKilograms(first), TestContext.Current.CancellationToken);

        await save.Should().ThrowAsync<Exception>();
        (await RawCurrent.Find(new BsonDocument("_id", first.Id)).SingleAsync(TestContext.Current.CancellationToken))
            .Equals(original)
            .Should()
            .BeTrue();
        (
            await History.CountDocumentsAsync(
                FilterDefinition<ObligationCalculationHistory>.Empty,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(0);
        (await Events(first.Id)).Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_WhenCompetingCallsReadSameVersion_ShouldSurfaceConflict(bool firstInsert)
    {
        var input = Calculation();
        if (!firstInsert)
            await Subject().Save(input, TestContext.Current.CancellationToken);
        var competing = firstInsert ? input : ChangeKilograms(input);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedContext = Context();
        var wrapper = Wrapper(pausedContext);
        wrapper
            .ExecuteTransaction(
                Arg.Any<Func<IClientSessionHandle, CancellationToken, Task<ObligationCalculation>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
            {
                var attempts = 0;

                return pausedContext.ExecuteTransaction(
                    async (session, token) =>
                    {
                        if (attempts++ == 0)
                        {
                            reached.SetResult();
                            await release.Task.WaitAsync(token);
                            var transient = new MongoException("Injected transient transaction failure");
                            transient.AddErrorLabel("TransientTransactionError");
                            throw transient;
                        }

                        return await call.ArgAt<
                            Func<IClientSessionHandle, CancellationToken, Task<ObligationCalculation>>
                        >(0)(session, token);
                    },
                    call.ArgAt<string>(1),
                    call.ArgAt<CancellationToken>(2)
                );
            });
        var losing = Subject(wrapper).Save(competing, TestContext.Current.CancellationToken);
        await reached.Task.WaitAsync(TestContext.Current.CancellationToken);
        var winner = await Subject().Save(competing, TestContext.Current.CancellationToken);
        release.SetResult();
        Func<Task> finish = async () => await losing;

        var conflict = await finish.Should().ThrowAsync<ConcurrencyException>();
        if (firstInsert)
            conflict.Which.InnerException.Should().BeOfType<MongoWriteException>();
        var retried = await Subject().Save(competing, TestContext.Current.CancellationToken);
        retried.Should().BeEquivalentTo(winner);
        (
            await Current.CountDocumentsAsync(
                FilterDefinition<ObligationCalculation>.Empty,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(1);
        (
            await History.CountDocumentsAsync(
                FilterDefinition<ObligationCalculationHistory>.Empty,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(firstInsert ? 0 : 1);
        var events = await Events(winner.Id);
        events.Should().HaveCount(firstInsert ? 1 : 2);
        (
            await AuditEvents.CountDocumentsAsync(
                x => x.Entity == "obligation_calculation",
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(firstInsert ? 1 : 2);
        events.Last().After.Should().Be(winner.ToBsonDocument());
    }

    [Fact]
    public async Task Save_WhenReplacementFailsAfterArchive_ShouldRollbackArchive()
    {
        var collectionName = $"calculation_rollback_{Guid.NewGuid():N}";
        await GetMongoDatabase()
            .CreateCollectionAsync(
                collectionName,
                new CreateCollectionOptions<ObligationCalculation>
                {
                    Validator = Builders<ObligationCalculation>.Filter.Eq(x => x.Version, 1),
                },
                TestContext.Current.CancellationToken
            );
        var collection = GetMongoApplicationDatabase().GetCollection<ObligationCalculation>(collectionName);
        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<ObligationCalculation>(
                Builders<ObligationCalculation>
                    .IndexKeys.Ascending(x => x.SubmitterOrganisationId)
                    .Ascending(x => x.ObligationYear),
                new CreateIndexOptions { Unique = true }
            ),
            cancellationToken: TestContext.Current.CancellationToken
        );
        try
        {
            var context = Context();
            var wrapper = Wrapper(context);
            wrapper.ObligationCalculations.Returns(collection);
            wrapper
                .ExecuteTransaction(
                    Arg.Any<Func<IClientSessionHandle, CancellationToken, Task<ObligationCalculation>>>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(call =>
                    context.ExecuteTransaction(
                        call.ArgAt<Func<IClientSessionHandle, CancellationToken, Task<ObligationCalculation>>>(0),
                        call.ArgAt<string>(1),
                        call.ArgAt<CancellationToken>(2)
                    )
                );
            var subject = Subject(wrapper);
            var first = await subject.Save(Calculation(), TestContext.Current.CancellationToken);
            Func<Task> save = () => subject.Save(ChangeKilograms(first), TestContext.Current.CancellationToken);

            await save.Should().ThrowAsync<MongoWriteException>();
            (
                await GetMongoDatabase()
                    .GetCollection<ObligationCalculation>(collectionName)
                    .Find(x => x.Id == first.Id)
                    .SingleAsync(TestContext.Current.CancellationToken)
            )
                .Should()
                .BeEquivalentTo(first);
            (
                await History.CountDocumentsAsync(
                    FilterDefinition<ObligationCalculationHistory>.Empty,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
                .Should()
                .Be(0);
            (await Events(first.Id)).Should().ContainSingle();
        }
        finally
        {
            await GetMongoDatabase().DropCollectionAsync(collectionName, TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_WhenTransactionFailsOrIsCancelledBeforeCommit_ShouldRollback(bool cancelled)
    {
        var first = await Subject().Save(Calculation(), TestContext.Current.CancellationToken);
        var context = Context();
        var wrapper = Wrapper(context);
        wrapper
            .ExecuteTransaction(
                Arg.Any<Func<IClientSessionHandle, CancellationToken, Task<ObligationCalculation>>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call =>
                context.ExecuteTransaction<ObligationCalculation>(
                    async (session, token) =>
                    {
                        await call.ArgAt<Func<IClientSessionHandle, CancellationToken, Task<ObligationCalculation>>>(0)(
                            session,
                            token
                        );
                        if (cancelled)
                            throw new OperationCanceledException(new CancellationToken(true));
                        throw new InvalidOperationException("Injected failure before commit");
                    },
                    call.ArgAt<string>(1),
                    call.ArgAt<CancellationToken>(2)
                )
            );
        Func<Task> save = () => Subject(wrapper).Save(ChangeKilograms(first), TestContext.Current.CancellationToken);

        if (cancelled)
            await save.Should().ThrowAsync<OperationCanceledException>();
        else
            await save.Should().ThrowAsync<InvalidOperationException>();
        (await Current.Find(x => x.Id == first.Id).SingleAsync(TestContext.Current.CancellationToken))
            .Should()
            .BeEquivalentTo(first);
        (
            await History.CountDocumentsAsync(
                FilterDefinition<ObligationCalculationHistory>.Empty,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(0);
        (await Events(first.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Save_WhenUnrelatedHistoryUniquenessFails_ShouldPreserveMongoFailureAndCurrent()
    {
        var first = await Subject().Save(Calculation(), TestContext.Current.CancellationToken);
        await History.InsertOneAsync(
            new ObligationCalculationHistory { Calculation = first },
            cancellationToken: TestContext.Current.CancellationToken
        );
        Func<Task> save = () => Subject().Save(ChangeKilograms(first), TestContext.Current.CancellationToken);

        await save.Should().ThrowAsync<MongoWriteException>();
        (await Current.Find(x => x.Id == first.Id).SingleAsync(TestContext.Current.CancellationToken))
            .Should()
            .BeEquivalentTo(first);
        (await Events(first.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Indexes_ShouldSupportCurrentAndHistoryReadShapes()
    {
        var first = await Subject().Save(Calculation(), TestContext.Current.CancellationToken);
        await Subject().Save(ChangeKilograms(first), TestContext.Current.CancellationToken);
        var application = Context();
        var current = application.ObligationCalculations;
        (
            await current
                .Find(x => x.SubmitterOrganisationId == first.SubmitterOrganisationId)
                .ToListAsync(TestContext.Current.CancellationToken)
        )
            .Should()
            .ContainSingle();
        var filter =
            Builders<ObligationCalculation>.Filter.In(
                x => x.SubmitterOrganisationId,
                new[] { first.SubmitterOrganisationId, Guid.NewGuid() }
            ) & Builders<ObligationCalculation>.Filter.Eq(x => x.ObligationYear, first.ObligationYear);
        (await current.Find(filter).ToListAsync(TestContext.Current.CancellationToken)).Should().ContainSingle();
        (
            await current
                .Find(
                    Builders<ObligationCalculation>.Filter.ElemMatch(
                        x => x.Producers,
                        x => x.ProducerOrganisationId == first.Producers[0].ProducerOrganisationId
                    )
                )
                .ToListAsync(TestContext.Current.CancellationToken)
        )
            .Should()
            .ContainSingle();
        (await current.Find(x => x.Id == first.Id).SingleAsync(TestContext.Current.CancellationToken))
            .Version.Should()
            .Be(2);
        (
            await application
                .ObligationCalculationHistory.Find(x => x.Calculation.Id == first.Id && x.Calculation.Version == 1)
                .SingleAsync(TestContext.Current.CancellationToken)
        )
            .Calculation.Should()
            .BeEquivalentTo(first);
        (
            await application
                .ObligationCalculationHistory.Find(x => x.Calculation.Id == first.Id)
                .SortBy(x => x.Calculation.Version)
                .ToListAsync(TestContext.Current.CancellationToken)
        )
            .Should()
            .ContainSingle();
    }

    private static IMongoCollection<ObligationCalculation> Current =>
        GetMongoDatabase().GetCollection<ObligationCalculation>(nameof(ObligationCalculation));
    private static IMongoCollection<BsonDocument> RawCurrent =>
        GetMongoDatabase().GetCollection<BsonDocument>(nameof(ObligationCalculation));
    private static IMongoCollection<ObligationCalculationHistory> History =>
        GetMongoDatabase().GetCollection<ObligationCalculationHistory>(nameof(ObligationCalculationHistory));

    private static ObligationCalculationService Subject(IDbContext? context = null, TimeProvider? timeProvider = null)
    {
        context ??= Context();

        return new ObligationCalculationService(
            context,
            new AuditEventService(
                new AuditEventDbContext(context.ObligationCalculations.Database),
                timeProvider ?? TimeProvider.System,
                new UlidEventIdGenerator()
            )
        );
    }

    private Task<List<AuditEvent>> Events(ObjectId id) =>
        AuditEvents
            .Find(x => x.Entity == "obligation_calculation" && x.EntityId == id.ToString())
            .SortBy(x => x.Version)
            .ToListAsync(TestContext.Current.CancellationToken);

    private static MongoDbContext Context() =>
        new(
            GetMongoApplicationDatabase(),
            Options.Create(new MongoDbOptions()),
            Substitute.For<ILogger<MongoDbContext>>()
        );

    private static IDbContext Wrapper(MongoDbContext context)
    {
        var wrapper = Substitute.For<IDbContext>();
        wrapper.ObligationCalculations.Returns(context.ObligationCalculations);
        wrapper.ObligationCalculationHistory.Returns(context.ObligationCalculationHistory);

        return wrapper;
    }

    private static ObligationCalculation ChangeKilograms(ObligationCalculation value) =>
        value with
        {
            CalculatedOn = value.CalculatedOn.AddMinutes(1),
            Producers =
            [
                value.Producers[0] with
                {
                    Materials =
                    [
                        value.Producers[0].Materials[0] with
                        {
                            InputKilograms = value.Producers[0].Materials[0].InputKilograms + 100,
                        },
                        .. value.Producers[0].Materials.Skip(1),
                    ],
                },
                value.Producers[1],
            ],
        };

    private static ObligationCalculation Calculation() =>
        ObligationCalculationFixture.Default().With(x => x.Id, ObjectId.Empty).Create();
}
