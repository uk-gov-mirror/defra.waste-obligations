using System.Collections.Concurrent;
using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.ScheduledJobs;
using Defra.WasteObligations.Api.Services.ObligationCalculations;
using Defra.WasteObligations.Api.Services.Scheduling;
using Defra.WasteObligations.Api.Services.WastePackaging;
using Defra.WasteObligations.Api.Utils.Logging;
using Defra.WasteObligations.Api.Utils.Metrics;
using Defra.WasteObligations.AuditEvents;
using Defra.WasteObligations.AuditEvents.Data;
using Defra.WasteObligations.AuditEvents.Logging;
using Defra.WasteObligations.Testing;
using Defra.WasteObligations.Testing.Fixtures.Entities;
using Defra.WasteObligations.Testing.Fixtures.WastePackaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace Defra.WasteObligations.Api.IntegrationTests.Services.ObligationCalculations;

public class ObligationCalculationInstructionProcessorTests : IntegrationTestBase
{
    private const string JobName = "ObligationCalculation";

    [Theory]
    [InlineData("originating-request", false)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(" ", true)]
    public async Task Worker_ShouldCarryRunCorrelationIntoEveryCalculationEvent(string? traceId, bool legacy)
    {
        var database = GetMongoApplicationDatabase();
        ObligationCalculationInstruction instruction;
        if (legacy)
        {
            instruction = ObligationCalculationInstructionFixture
                .Default()
                .With(x => x.CorrelationId, traceId)
                .Create();
            await ObligationCalculationInstructions.InsertOneAsync(
                instruction,
                cancellationToken: TestContext.Current.CancellationToken
            );
        }
        else
        {
            using var scope = CorrelationContext.Begin(NullLogger.Instance, traceId);
            instruction = await new ObligationCalculationInstructionService(database, TimeProvider.System).Create(
                2026,
                null,
                TestContext.Current.CancellationToken
            );
            if (traceId is null)
                instruction.CorrelationId.Should().MatchRegex("^[0-9a-f]{32}$");
            else
                instruction.CorrelationId.Should().Be(traceId);
        }
        var context = new MongoDbContext(
            database,
            Options.Create(new MongoDbOptions()),
            NullLogger<MongoDbContext>.Instance
        );
        var calculationService = new ObligationCalculationService(
            context,
            new AuditEventService(new AuditEventDbContext(database), TimeProvider.System, new UlidEventIdGenerator())
        );
        var packaging = Substitute.For<IWastePackagingService>();
        packaging
            .Read(instruction.ObligationYear - 1, Arg.Any<CancellationToken>())
            .Returns(
                new PackagingResponse
                {
                    Items =
                    [
                        ApprovedSubmissionFixture.Default().Create(),
                        ApprovedSubmissionFixture.Default().Create(),
                    ],
                }
            );
        var processor = new ObligationCalculationProcessor(
            packaging,
            new ObligationCalculator(
                Options.Create(
                    new ObligationCalculationOptions
                    {
                        Targets = new()
                        {
                            [instruction.ObligationYear] = new() { [ObligationMaterials.Plastic] = 0.57 },
                        },
                    }
                ),
                TimeProvider.System
            ),
            calculationService,
            Substitute.For<IObligationCalculationMetrics>(),
            TimeProvider.System,
            NullLogger<ObligationCalculationProcessor>.Instance
        );
        await using var services = Services(processor);
        var worker = services.GetRequiredService<IHostedService>();
        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await WaitForAsync(async () =>
                (await Read(instruction)).Status.Should().Be(ObligationCalculationInstructionStatus.Completed)
            );
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }

        var events = await AuditEvents
            .Find(x => x.Entity == "obligation_calculation")
            .ToListAsync(TestContext.Current.CancellationToken);
        var expectedTraceId = string.IsNullOrWhiteSpace(instruction.CorrelationId)
            ? instruction.Id.ToString()
            : instruction.CorrelationId;
        events.Should().HaveCount(2).And.AllSatisfy(x => x.TraceId.Should().Be(expectedTraceId));
        (await Read(instruction)).CorrelationId.Should().Be(instruction.CorrelationId);
    }

    [Fact]
    public async Task WorkSource_ShouldSelectOldestWorkAndCompleteOnlyAfterProcessing()
    {
        var newer = ObligationCalculationInstructionFixture.Default().With(x => x.SubmitterId, Guid.NewGuid()).Create();
        var older = ObligationCalculationInstructionFixture
            .Default()
            .With(x => x.CreatedAt, newer.CreatedAt.AddMinutes(-1))
            .Create();
        var terminal = ObligationCalculationInstructionFixture
            .Default()
            .With(x => x.Status, ObligationCalculationInstructionStatus.Completed)
            .With(x => x.CreatedAt, older.CreatedAt.AddMinutes(-1))
            .Create();
        await ObligationCalculationInstructions.InsertManyAsync(
            [newer, terminal, older],
            cancellationToken: TestContext.Current.CancellationToken
        );
        var source = new ObligationCalculationWorkSource(GetMongoApplicationDatabase());
        var logger = new RecordingLogger<ObligationCalculationProcessor>();
        var packaging = EmptyPackaging();
        var processor = new ObligationCalculationProcessor(
            packaging,
            Substitute.For<IObligationCalculator>(),
            Substitute.For<IObligationCalculationService>(),
            Substitute.For<IObligationCalculationMetrics>(),
            TimeProvider.System,
            logger
        );

        var work = await source.GetNext([], TestContext.Current.CancellationToken);
        work.Should().NotBeNull();
        work.Payload.Id.Should().Be(older.Id);
        (await Read(older)).Status.Should().Be(ObligationCalculationInstructionStatus.Pending);
        (await source.TryStart(work, TestContext.Current.CancellationToken)).Should().BeTrue();
        await processor.Process(work.Payload, TestContext.Current.CancellationToken);
        (await Read(older)).Status.Should().Be(ObligationCalculationInstructionStatus.Running);
        await source.Complete(work, TestContext.Current.CancellationToken);

        (await Read(older))
            .Should()
            .BeEquivalentTo(older with { Status = ObligationCalculationInstructionStatus.Completed });
        (await Read(newer)).Should().BeEquivalentTo(newer);
        (await Read(terminal)).Should().BeEquivalentTo(terminal);
        logger
            .Entries.Should()
            .ContainSingle(x => x.Level == LogLevel.Information && x.Message.Contains("No packaging data"));
        (await source.TryStart(work, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Theory]
    [InlineData(ObligationCalculationInstructionStatus.Completed)]
    [InlineData(ObligationCalculationInstructionStatus.Failed)]
    public async Task WorkSource_ShouldIgnoreTerminalInstructions(ObligationCalculationInstructionStatus status)
    {
        var instruction = ObligationCalculationInstructionFixture.Default().With(x => x.Status, status).Create();
        await ObligationCalculationInstructions.InsertOneAsync(
            instruction,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var source = new ObligationCalculationWorkSource(GetMongoApplicationDatabase());

        (await source.GetNext([], TestContext.Current.CancellationToken)).Should().BeNull();
        (await Read(instruction)).Should().BeEquivalentTo(instruction);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Workers_ShouldProcessEachPersistedInstructionOnceAcrossHosts(int instructionCount)
    {
        var instructions = Enumerable
            .Range(0, instructionCount)
            .Select(_ =>
                ObligationCalculationInstructionFixture.Default().With(x => x.SubmitterId, Guid.NewGuid()).Create()
            )
            .ToArray();
        await ObligationCalculationInstructions.InsertManyAsync(
            instructions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        ConcurrentBag<ObjectId> processed = [];
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = Substitute.For<IScheduledJobProcessor<ObligationCalculationInstruction>>();
        processor
            .Process(Arg.Any<ObligationCalculationInstruction>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                processed.Add(call.Arg<ObligationCalculationInstruction>().Id);
                if (processed.Count == instructionCount)
                    allStarted.TrySetResult();

                return finish.Task;
            });
        await using var first = Services(processor);
        await using var second = Services(processor);
        await using var third = Services(processor);
        var workers = new[] { first, second, third }.Select(x => x.GetRequiredService<IHostedService>()).ToArray();
        try
        {
            await Task.WhenAll(workers.Select(x => x.StartAsync(TestContext.Current.CancellationToken)));
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            foreach (var instruction in instructions)
                (await Read(instruction)).Status.Should().Be(ObligationCalculationInstructionStatus.Running);
            finish.SetResult();
            await WaitForAsync(async () =>
            {
                foreach (var instruction in instructions)
                    (await Read(instruction)).Status.Should().Be(ObligationCalculationInstructionStatus.Completed);
            });
        }
        finally
        {
            finish.TrySetResult();
            await Task.WhenAll(workers.Select(x => x.StopAsync(TestContext.Current.CancellationToken)));
        }

        processed.Should().BeEquivalentTo(instructions.Select(x => x.Id));
    }

    [Fact]
    public async Task Worker_ShouldRecoverRunningInstructionAfterItsPreviousLeaseExpires()
    {
        var instruction = ObligationCalculationInstructionFixture
            .Default()
            .With(x => x.Status, ObligationCalculationInstructionStatus.Running)
            .Create();
        await ObligationCalculationInstructions.InsertOneAsync(
            instruction,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var past = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-2));
        var previousOwner = new ScheduledJobLeaseService(
            GetMongoApplicationDatabase(),
            past,
            JobName,
            instruction.Id.ToString()
        );
        (await previousOwner.TryAcquire(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken))
            .Should()
            .BeTrue();
        await using var services = Services();
        var worker = services.GetRequiredService<IHostedService>();
        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await WaitForAsync(async () =>
                (await Read(instruction)).Status.Should().Be(ObligationCalculationInstructionStatus.Completed)
            );
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("trace-123")]
    public async Task Worker_WhenPackagingFetchFails_ShouldKeepSameInstructionAndRetryBeforeCompleting(string? traceId)
    {
        var instruction = ObligationCalculationInstructionFixture
            .Default()
            .With(x => x.CorrelationId, traceId)
            .Create();
        await ObligationCalculationInstructions.InsertOneAsync(
            instruction,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var observedTraceIds = new ConcurrentBag<string?>();
        var packaging = Substitute.For<IWastePackagingService>();
        packaging
            .Read(instruction.ObligationYear - 1, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                observedTraceIds.Add(CorrelationContext.Current);
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new HttpRequestException("Packaging API unavailable");
                retryStarted.TrySetResult();
                await finish.Task;

                return new PackagingResponse { Items = [] };
            });
        var processor = new ObligationCalculationProcessor(
            packaging,
            Substitute.For<IObligationCalculator>(),
            Substitute.For<IObligationCalculationService>(),
            Substitute.For<IObligationCalculationMetrics>(),
            TimeProvider.System,
            new RecordingLogger<ObligationCalculationProcessor>()
        );
        var workerLogger = Substitute.For<ILogger<ScheduledJobProcessingWorker<ObligationCalculationInstruction>>>();
        await using var services = Services(processor, workerLogger);
        var worker = services.GetRequiredService<IHostedService>();
        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            (await Read(instruction)).Status.Should().Be(ObligationCalculationInstructionStatus.Running);
            finish.SetResult();
            await WaitForAsync(async () =>
                (await Read(instruction)).Status.Should().Be(ObligationCalculationInstructionStatus.Completed)
            );
        }
        finally
        {
            finish.TrySetResult();
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
        workerLogger
            .Received()
            .Log(
                LogLevel.Error,
                Arg.Any<EventId>(),
                Arg.Any<object>(),
                Arg.Is<Exception>(exception =>
                    exception is HttpRequestException && exception.Message == "Packaging API unavailable"
                ),
                Arg.Any<Func<object, Exception?, string>>()
            );
        attempts.Should().Be(2);
        await packaging.Received(2).Read(instruction.ObligationYear - 1, Arg.Any<CancellationToken>());
        var expectedTraceId = string.IsNullOrWhiteSpace(traceId) ? instruction.Id.ToString() : traceId;
        observedTraceIds.Should().HaveCount(2).And.OnlyContain(x => x == expectedTraceId);
        workerLogger
            .Received()
            .BeginScope(Arg.Is<Dictionary<string, object?>>(x => (string?)x["TraceId"] == expectedTraceId));
        (await Read(instruction)).FailureCount.Should().Be(1);
        (
            await ObligationCalculationInstructions.CountDocumentsAsync(
                FilterDefinition<ObligationCalculationInstruction>.Empty,
                cancellationToken: TestContext.Current.CancellationToken
            )
        )
            .Should()
            .Be(1);
    }

    [Fact]
    public async Task Worker_WhenOldestInstructionKeepsFailing_ShouldCompleteLaterWorkBeforeRetrying()
    {
        var oldest = ObligationCalculationInstructionFixture.Default().Create();
        var later = ObligationCalculationInstructionFixture
            .Default()
            .With(x => x.SubmitterId, Guid.NewGuid())
            .With(x => x.CreatedAt, oldest.CreatedAt.AddMinutes(1))
            .Create();
        await ObligationCalculationInstructions.InsertManyAsync(
            [oldest, later],
            cancellationToken: TestContext.Current.CancellationToken
        );
        ConcurrentQueue<ObjectId> attempts = [];
        var failure = new InvalidOperationException("This instruction always fails");
        var processor = Substitute.For<IScheduledJobProcessor<ObligationCalculationInstruction>>();
        processor
            .Process(Arg.Any<ObligationCalculationInstruction>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var instruction = call.Arg<ObligationCalculationInstruction>();
                attempts.Enqueue(instruction.Id);

                return instruction.Id == oldest.Id ? Task.FromException(failure) : Task.CompletedTask;
            });
        var logger = new RecordingLogger<ScheduledJobProcessingWorker<ObligationCalculationInstruction>>();
        await using var services = Services(processor, logger);
        var worker = services.GetRequiredService<IHostedService>();
        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await WaitForAsync(() =>
            {
                attempts.Count.Should().BeGreaterThanOrEqualTo(3);

                return Task.CompletedTask;
            });
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }

        attempts.Take(3).Should().Equal(oldest.Id, later.Id, oldest.Id);
        (await Read(oldest)).Status.Should().Be(ObligationCalculationInstructionStatus.Running);
        (await Read(later)).Status.Should().Be(ObligationCalculationInstructionStatus.Completed);
        logger
            .Entries.Should()
            .Contain(x => ReferenceEquals(x.Exception, failure) && x.Message.Contains(oldest.Id.ToString()));
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData(2, 2)]
    public async Task Worker_WhenFailureLimitReachedAcrossHosts_ShouldPermanentlyFailInstruction(
        int? maximumAttempts,
        int expectedFailures
    )
    {
        var instruction = ObligationCalculationInstructionFixture.Default().Create();
        await ObligationCalculationInstructions.InsertOneAsync(
            instruction,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var attempts = 0;
        var processor = Substitute.For<IScheduledJobProcessor<ObligationCalculationInstruction>>();
        processor
            .Process(Arg.Any<ObligationCalculationInstruction>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref attempts);

                return Task.FromException(new InvalidOperationException("Always fails"));
            });
        await using (var firstHost = Services(processor, maximumAttempts: maximumAttempts, pollIntervalSeconds: 3600))
        {
            var worker = firstHost.GetRequiredService<IHostedService>();
            try
            {
                await worker.StartAsync(TestContext.Current.CancellationToken);
                await WaitForAsync(async () => (await Read(instruction)).FailureCount.Should().Be(1));
            }
            finally
            {
                await worker.StopAsync(TestContext.Current.CancellationToken);
            }
        }
        (await Read(instruction)).Status.Should().Be(ObligationCalculationInstructionStatus.Running);
        await using (var secondHost = Services(processor, maximumAttempts: maximumAttempts))
        {
            var worker = secondHost.GetRequiredService<IHostedService>();
            try
            {
                await worker.StartAsync(TestContext.Current.CancellationToken);
                await WaitForAsync(async () =>
                    (await Read(instruction)).Status.Should().Be(ObligationCalculationInstructionStatus.Failed)
                );
            }
            finally
            {
                await worker.StopAsync(TestContext.Current.CancellationToken);
            }
        }

        attempts.Should().Be(expectedFailures);
        (await Read(instruction)).FailureCount.Should().Be(expectedFailures);
        var source = new ObligationCalculationWorkSource(GetMongoApplicationDatabase());
        (await source.GetNext([], TestContext.Current.CancellationToken)).Should().BeNull();
        (await source.TryStart(new(instruction.Id.ToString(), instruction), TestContext.Current.CancellationToken))
            .Should()
            .BeFalse();
        using var session = await GetMongoDatabase()
            .Client.StartSessionAsync(cancellationToken: TestContext.Current.CancellationToken);
        var next = await new ObligationCalculationInstructionService(
            GetMongoApplicationDatabase(),
            TimeProvider.System
        ).Create(session, instruction.ObligationYear, instruction.SubmitterId, TestContext.Current.CancellationToken);
        next.Id.Should().NotBe(instruction.Id);
        next.FailureCount.Should().Be(0);
        (await Read(instruction)).Status.Should().Be(ObligationCalculationInstructionStatus.Failed);
    }

    [Fact]
    public async Task RecordFailure_WhenLegacyInstructionHasNoCount_ShouldCountFailuresAndFailAtLimit()
    {
        var id = ObjectId.GenerateNewId();
        await GetMongoDatabase()
            .GetCollection<BsonDocument>(ObligationCalculationInstruction.CollectionName)
            .InsertOneAsync(
                new BsonDocument
                {
                    { "_id", id },
                    { "obligationYear", 2026 },
                    { "submitterId", BsonNull.Value },
                    { "status", nameof(ObligationCalculationInstructionStatus.Running) },
                    { "createdAt", DateTime.UtcNow },
                },
                cancellationToken: TestContext.Current.CancellationToken
            );
        var source = new ObligationCalculationWorkSource(GetMongoApplicationDatabase());
        var work = await source.GetNext([], TestContext.Current.CancellationToken);
        work.Should().NotBeNull();
        work.Payload.FailureCount.Should().Be(0);

        await source.RecordFailure(work, 3, TestContext.Current.CancellationToken);
        (await Read(work.Payload)).FailureCount.Should().Be(1);
        (await Read(work.Payload)).Status.Should().Be(ObligationCalculationInstructionStatus.Running);
        await source.RecordFailure(work, 3, TestContext.Current.CancellationToken);
        (await Read(work.Payload)).Status.Should().Be(ObligationCalculationInstructionStatus.Running);
        await source.RecordFailure(work, 3, TestContext.Current.CancellationToken);

        var stored = await Read(work.Payload);
        stored.FailureCount.Should().Be(3);
        stored.Status.Should().Be(ObligationCalculationInstructionStatus.Failed);
        (await source.GetNext([], TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_ShouldCompleteByConventionAndAllowFreshInstruction(bool submitterSpecific)
    {
        Guid? submitterId = submitterSpecific ? Guid.NewGuid() : null;
        var creation = new ObligationCalculationInstructionService(GetMongoApplicationDatabase(), TimeProvider.System);
        using var session = await GetMongoDatabase()
            .Client.StartSessionAsync(cancellationToken: TestContext.Current.CancellationToken);
        var first = await creation.Create(session, 2026, submitterId, TestContext.Current.CancellationToken);
        await using var services = Services();
        var worker = services.GetRequiredService<IHostedService>();
        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await WaitForAsync(async () =>
                (await Read(first)).Status.Should().Be(ObligationCalculationInstructionStatus.Completed)
            );
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
        var next = await creation.Create(session, 2026, submitterId, TestContext.Current.CancellationToken);
        next.Id.Should().NotBe(first.Id);
        next.Status.Should().Be(ObligationCalculationInstructionStatus.Pending);
    }

    private static ServiceProvider Services(
        IScheduledJobProcessor<ObligationCalculationInstruction>? processor = null,
        ILogger<ScheduledJobProcessingWorker<ObligationCalculationInstruction>>? logger = null,
        int? maximumAttempts = null,
        int pollIntervalSeconds = 1
    )
    {
        var settings = new Dictionary<string, string?>
        {
            ["ObligationCalculation:Enabled"] = "false",
            ["ObligationCalculation:Processing:Enabled"] = "true",
            ["ObligationCalculation:Processing:PollIntervalSeconds"] = $"{pollIntervalSeconds}",
        };
        if (maximumAttempts is { } limit)
            settings["ObligationCalculation:Processing:MaximumAttempts"] = $"{limit}";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(GetMongoApplicationDatabase())
            .AddSingleton(EmptyPackaging())
            .AddSingleton(Substitute.For<IObligationCalculator>())
            .AddSingleton(Substitute.For<IObligationCalculationService>())
            .AddRequestMetrics()
            .AddScheduledJobs(configuration);
        if (processor is not null)
            services.AddScoped(_ => processor);
        if (logger is not null)
            services.AddSingleton(logger);

        return services.BuildServiceProvider();
    }

    private static IWastePackagingService EmptyPackaging()
    {
        var packaging = Substitute.For<IWastePackagingService>();
        packaging.Read(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PackagingResponse { Items = [] });

        return packaging;
    }

    private Task<ObligationCalculationInstruction> Read(ObligationCalculationInstruction instruction) =>
        ObligationCalculationInstructions
            .Find(x => x.Id == instruction.Id)
            .SingleAsync(TestContext.Current.CancellationToken);
}
