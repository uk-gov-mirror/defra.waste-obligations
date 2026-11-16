using System.Threading.Channels;
using AutoFixture;
using AwesomeAssertions;
using Cronos;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Services;
using Defra.WasteObligations.Api.Services.ObligationCalculations;
using Defra.WasteObligations.Api.Services.Scheduling;
using Defra.WasteObligations.Api.Services.WastePackaging;
using Defra.WasteObligations.Api.Utils.Logging;
using Defra.WasteObligations.Api.Utils.Metrics;
using Defra.WasteObligations.AuditEvents;
using Defra.WasteObligations.Testing;
using Defra.WasteObligations.Testing.Extensions.WireMock;
using Defra.WasteObligations.Testing.Fixtures.WastePackaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using WireMock.Admin.Mappings;
using WireMock.Admin.Requests;
using WireMock.Client.Extensions;

namespace Defra.WasteObligations.Api.IntegrationTests.Services.Scheduling;

public class ScheduledJobTests : IntegrationTestBase
{
    private const string JobName = "test-scheduled-job";
    private const string LeaseCollection = "_scheduled_job_leases";
    private const string StateCollection = "_scheduled_job_state";
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 30, 0, 59, 0, TimeSpan.Zero));

    [Fact]
    public async Task ConfiguredJob_ShouldFetchPreviousPackagingYearAndPersistCalculatedMaterials()
    {
        await Reset();
        const string path = "/packaging/2025/producer-material-totals-by-submitter";
        var submitterId = Guid.NewGuid();
        var producerId = Guid.NewGuid();
        var time = new SchedulerTimeProvider(_clock);
        var settings = new Dictionary<string, string?>
        {
            ["ScheduledJobs:ObligationCalculation:Enabled"] = "true",
            ["ScheduledJobs:ObligationCalculation:Cron"] = "0 1 * * *",
            ["ScheduledJobs:ObligationCalculation:Processing:Enabled"] = "true",
            ["WastePackaging:BaseAddress"] = "http://localhost:9090/",
            ["WastePackaging:ClientId"] = "client",
            ["WastePackaging:ClientSecret"] = "secret",
            ["ObligationCalculation:Targets:2026:PL"] = "0.57",
            ["ObligationCalculation:Targets:2026:PC"] = "0.77",
            ["ObligationCalculation:Targets:2026:FC"] = "0.77",
            ["ObligationCalculation:Targets:2026:AL"] = "0.62",
            ["ObligationCalculation:Targets:2026:ST"] = "0.81",
            ["ObligationCalculation:Targets:2026:WD"] = "0.46",
            ["ObligationCalculation:Targets:2026:GL"] = "0.76",
            ["ObligationCalculation:Targets:2026:GR"] = "0.76",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        await using var host = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton(GetMongoApplicationDatabase())
            .AddSingleton<TimeProvider>(time)
            .AddOptions()
            .Configure<TraceHeader>(x => x.Name = TraceHeaderName)
            .AddScoped<IDbContext, MongoDbContext>()
            .AddSingleton<ICurrentObligationYearProvider, CurrentObligationYearProvider>()
            .AddTransient<IObligationCalculationInstructionService, ObligationCalculationInstructionService>()
            .AddWastePackagingService(false)
            .AddRequestMetrics()
            .AddAuditEvents(configuration, addAnalyticsProcessor: false)
            .AddObligationCalculation()
            .AddScheduledJobs(configuration.GetSection("ScheduledJobs"))
            .BuildServiceProvider();
        var workers = host.GetServices<IHostedService>().ToArray();
        var scheduler = workers.OfType<ScheduledJobWorker>().Single();
        var processor = workers.OfType<ScheduledJobProcessingWorker<ObligationCalculationInstruction>>().Single();
        try
        {
            await scheduler.StartAsync(TestContext.Current.CancellationToken);
            await time.WaitForTimer(TimeSpan.FromMinutes(1));
            _clock.Advance(TimeSpan.FromMinutes(1));
            await time.WaitForTimer(TimeSpan.FromDays(1));
            var instruction = await ObligationCalculationInstructions
                .Find(x => x.ObligationYear == 2026)
                .SingleAsync(TestContext.Current.CancellationToken);
            instruction.SubmitterId.Should().BeNull();
            instruction.CorrelationId.Should().NotBeNullOrWhiteSpace();
            await WireMockContext.WireMockAdminApi.StubWastePackagingRequest(
                2025,
                new PackagingResponse
                {
                    Items =
                    [
                        ApprovedSubmissionFixture
                            .Default()
                            .With(x => x.SubmitterOrganisationId, submitterId)
                            .With(x => x.SubmitterType, ObligationSubmitterType.ComplianceScheme)
                            .With(
                                x => x.Producers,
                                [
                                    ProducerPackagingFixture
                                        .Default()
                                        .With(x => x.ProducerOrganisationId, producerId)
                                        .Create(),
                                ]
                            )
                            .Create(),
                    ],
                },
                basicAuthToken: "Y2xpZW50OnNlY3JldA==",
                traceId: instruction.CorrelationId
            );
            await processor.StartAsync(TestContext.Current.CancellationToken);
            await WaitForAsync(async () =>
                (
                    await ObligationCalculationInstructions
                        .Find(x => x.Id == instruction.Id)
                        .SingleAsync(TestContext.Current.CancellationToken)
                )
                    .Status.Should()
                    .Be(ObligationCalculationInstructionStatus.Completed)
            );

            var calculation = await ObligationCalculations
                .Find(x => x.SubmitterOrganisationId == submitterId && x.ObligationYear == 2026)
                .SingleAsync(TestContext.Current.CancellationToken);
            calculation.SubmitterType.Should().Be(ObligationSubmitterType.ComplianceScheme);
            var producer = calculation.Producers.Should().ContainSingle().Which;
            producer.ProducerOrganisationId.Should().Be(producerId);
            producer
                .Materials.Should()
                .ContainSingle()
                .Which.Should()
                .BeEquivalentTo(
                    new
                    {
                        MaterialCode = ObligationMaterials.Plastic,
                        InputKilograms = 10000L,
                        InputTonnes = 10L,
                        ObligationTonnes = 6L,
                    }
                );
            var requests = await WireMockContext.WireMockAdminApi.FindRequestsAsync(
                new RequestModel { Methods = ["GET"], Path = path },
                TestContext.Current.CancellationToken
            );
            requests.Should().ContainSingle();
        }
        finally
        {
            try
            {
                await processor.StopAsync(CancellationToken.None);
            }
            finally
            {
                await scheduler.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task ConfiguredJob_WhenPackagingFails_ShouldCreateNewInstructionOnNextDay()
    {
        await Reset();
        var time = new SchedulerTimeProvider(_clock);
        var settings = new Dictionary<string, string?>
        {
            ["ObligationCalculation:Enabled"] = "true",
            ["ObligationCalculation:Cron"] = "0 1 * * *",
            ["ObligationCalculation:Processing:Enabled"] = "true",
            ["ObligationCalculation:Processing:MaximumAttempts"] = "1",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var packaging = Substitute.For<IWastePackagingService>();
        packaging
            .Read(2025, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<PackagingResponse>(new HttpRequestException("Packaging API unavailable")));
        await using var host = new ServiceCollection()
            .AddLogging()
            .AddSingleton(GetMongoApplicationDatabase())
            .AddSingleton<TimeProvider>(time)
            .AddOptions()
            .AddScoped<IDbContext, MongoDbContext>()
            .AddSingleton<ICurrentObligationYearProvider, CurrentObligationYearProvider>()
            .AddTransient<IObligationCalculationInstructionService, ObligationCalculationInstructionService>()
            .AddSingleton(packaging)
            .AddSingleton(Substitute.For<IObligationCalculator>())
            .AddSingleton(Substitute.For<IObligationCalculationService>())
            .AddScheduledJobs(configuration)
            .BuildServiceProvider();
        var workers = host.GetServices<IHostedService>().ToArray();
        var scheduler = workers.OfType<ScheduledJobWorker>().Single();
        var processor = workers.OfType<ScheduledJobProcessingWorker<ObligationCalculationInstruction>>().Single();
        try
        {
            await scheduler.StartAsync(TestContext.Current.CancellationToken);
            await time.WaitForTimer(TimeSpan.FromMinutes(1));
            _clock.Advance(TimeSpan.FromMinutes(1));
            await time.WaitForTimer(TimeSpan.FromDays(1));
            var first = await ObligationCalculationInstructions
                .Find(x => x.ObligationYear == 2026)
                .SingleAsync(TestContext.Current.CancellationToken);
            await processor.StartAsync(TestContext.Current.CancellationToken);
            await WaitForAsync(async () =>
                (
                    await ObligationCalculationInstructions
                        .Find(x => x.Id == first.Id)
                        .SingleAsync(TestContext.Current.CancellationToken)
                )
                    .Status.Should()
                    .Be(ObligationCalculationInstructionStatus.Failed)
            );
            await processor.StopAsync(TestContext.Current.CancellationToken);
            _clock.Advance(TimeSpan.FromDays(1));
            await WaitForAsync(async () =>
                (
                    await ObligationCalculationInstructions.CountDocumentsAsync(
                        FilterDefinition<ObligationCalculationInstruction>.Empty,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                )
                    .Should()
                    .Be(2)
            );
            var next = await ObligationCalculationInstructions
                .Find(x => x.Id != first.Id)
                .SingleAsync(TestContext.Current.CancellationToken);
            next.Status.Should().Be(ObligationCalculationInstructionStatus.Pending);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(2027, 1, 31, 2026)]
    [InlineData(2027, 2, 1, 2027)]
    public async Task ConfiguredJob_ShouldCreateInstructionForCurrentObligationYear(
        int year,
        int month,
        int day,
        int expectedYear
    )
    {
        await Reset();
        _clock.SetUtcNow(new DateTimeOffset(year, month, day, 0, 59, 0, TimeSpan.Zero));
        var time = new SchedulerTimeProvider(_clock);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ObligationCalculation:Enabled"] = "true",
                    ["ObligationCalculation:Cron"] = "0 1 * * *",
                }
            )
            .Build();
        await using var host = new ServiceCollection()
            .AddLogging()
            .AddSingleton(GetMongoApplicationDatabase())
            .AddSingleton<TimeProvider>(time)
            .AddOptions()
            .AddScoped<IDbContext, MongoDbContext>()
            .AddSingleton<ICurrentObligationYearProvider, CurrentObligationYearProvider>()
            .AddTransient<IObligationCalculationInstructionService, ObligationCalculationInstructionService>()
            .AddScheduledJobs(configuration)
            .BuildServiceProvider();
        var worker = host.GetRequiredService<IHostedService>();

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await time.WaitForTimer(TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await time.WaitForTimer(TimeSpan.FromDays(1));

        var instruction = await ObligationCalculationInstructions
            .Find(x => x.ObligationYear == expectedYear)
            .SingleAsync(TestContext.Current.CancellationToken);
        instruction.SubmitterId.Should().BeNull();
        instruction.Status.Should().Be(ObligationCalculationInstructionStatus.Pending);
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Shutdown_ShouldKeepRenewingLeaseUntilCancelledJobFinishes()
    {
        await Reset();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new JobControl
        {
            Run = async token =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                await finish.Task;
            },
        };
        var time = new SchedulerTimeProvider(_clock);
        await using var host = CreateHost(time, control, "* * * * *");
        var worker = host.GetRequiredService<IHostedService>();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await time.WaitForTimer(TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() =>
        {
            control.Count.Should().Be(1);
            return Task.CompletedTask;
        });
        var initialExpiry = (await ReadLease())["expiresAt"].ToUniversalTime();

        var stopping = worker.StopAsync(TestContext.Current.CancellationToken);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(40));
        await WaitForAsync(async () =>
            (await ReadLease())["expiresAt"].ToUniversalTime().Should().BeAfter(initialExpiry)
        );

        stopping.IsCompleted.Should().BeFalse();
        control.Count.Should().Be(1);
        finish.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        (await ReadLease()).Contains("owner").Should().BeFalse();
    }

    [Fact]
    public async Task Schedule_ShouldRunOnceAcrossHostsIncludingAHostCheckingAfterCompletion()
    {
        await Reset();
        var control = new JobControl();
        var firstTime = new SchedulerTimeProvider(_clock);
        var secondTime = new SchedulerTimeProvider(_clock);
        await using var first = CreateHost(firstTime, control);
        await using var second = CreateHost(secondTime, control);
        var firstWorker = first.GetRequiredService<IHostedService>();
        var secondWorker = second.GetRequiredService<IHostedService>();
        await firstWorker.StartAsync(TestContext.Current.CancellationToken);
        await secondWorker.StartAsync(TestContext.Current.CancellationToken);
        await firstTime.WaitForTimer(TimeSpan.FromMinutes(1));
        await secondTime.WaitForTimer(TimeSpan.FromMinutes(1));
        control.Count.Should().Be(0);

        _clock.Advance(TimeSpan.FromMinutes(1));
        var retry = await Task.WhenAny(firstTime.RetryTimer, secondTime.RetryTimer)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await WaitForAsync(async () =>
        {
            control.Count.Should().Be(1);
            var committed = await GetMongoDatabase()
                .GetCollection<BsonDocument>(StateCollection)
                .Find(new BsonDocument("_id", JobName))
                .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
            committed.Should().NotBeNull();
            (await ReadLease()).Contains("owner").Should().BeFalse();
        });
        _clock.Advance(TimeSpan.FromSeconds(5));
        var retryingHost = retry == firstTime.RetryTimer ? firstTime : secondTime;
        await retryingHost.WaitForTimer(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(5));

        control.Count.Should().Be(1);
        var state = await GetMongoDatabase()
            .GetCollection<BsonDocument>(StateCollection)
            .Find(new BsonDocument("_id", JobName))
            .SingleAsync(TestContext.Current.CancellationToken);
        state["lastStartedAt"].ToUniversalTime().Should().Be(_clock.GetUtcNow().AddSeconds(-5).UtcDateTime);

        var lateClock = new FakeTimeProvider(_clock.GetUtcNow().AddMinutes(-1).AddSeconds(-5));
        var lateTime = new SchedulerTimeProvider(lateClock);
        await using var late = CreateHost(lateTime, control);
        var lateWorker = late.GetRequiredService<IHostedService>();
        await lateWorker.StartAsync(TestContext.Current.CancellationToken);
        await lateTime.WaitForTimer(TimeSpan.FromMinutes(1));
        lateClock.Advance(TimeSpan.FromMinutes(1));
        await lateTime.WaitForTimer(TimeSpan.FromSeconds(5));
        lateClock.Advance(TimeSpan.FromSeconds(5));
        await lateTime.WaitForTimer(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(5));

        control.Count.Should().Be(1);
        await lateWorker.StopAsync(TestContext.Current.CancellationToken);
        await firstWorker.StopAsync(TestContext.Current.CancellationToken);
        await secondWorker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Schedule_WhenLeaseHolderStopsBeforeHandoff_ShouldRetryTheDueOccurrence()
    {
        await Reset();
        var lease = Substitute.For<IScheduledJobLeaseService>();
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(false, true);
        lease.TryRenew(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        var control = new JobControl();
        var time = new SchedulerTimeProvider(_clock);
        await using var host = CreateHost(time, control, leaseService: lease, transactional: true);
        var worker = host.GetRequiredService<IHostedService>();
        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await time.WaitForTimer(TimeSpan.FromMinutes(1));
            _clock.Advance(TimeSpan.FromMinutes(1));
            await WaitForAsync(async () =>
                await lease.Received(1).TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            );
            await time.WaitForTimer(TimeSpan.FromSeconds(5));
            _clock.Advance(TimeSpan.FromSeconds(5));
            await WaitForAsync(
                () =>
                {
                    control.Count.Should().Be(1);
                    return Task.CompletedTask;
                },
                timeout: 5
            );
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Schedule_WhenClaimedHandoffFails_ShouldAllowAnotherHostToRetry()
    {
        await Reset();
        var firstClock = new FakeTimeProvider(_clock.GetUtcNow());
        var firstTime = new SchedulerTimeProvider(firstClock);
        var firstControl = new JobControl
        {
            Run = _ => Task.FromException(new InvalidOperationException("Handoff interrupted")),
        };
        await using var first = CreateHost(firstTime, firstControl, transactional: true);
        var firstWorker = first.GetRequiredService<IHostedService>();
        var secondClock = new FakeTimeProvider(_clock.GetUtcNow());
        var secondTime = new SchedulerTimeProvider(secondClock);
        var secondControl = new JobControl();
        await using var second = CreateHost(secondTime, secondControl, transactional: true);
        var secondWorker = second.GetRequiredService<IHostedService>();
        try
        {
            await firstWorker.StartAsync(TestContext.Current.CancellationToken);
            await firstTime.WaitForTimer(TimeSpan.FromMinutes(1));
            firstClock.Advance(TimeSpan.FromMinutes(1));
            await firstTime.WaitForTimer(TimeSpan.FromSeconds(5));
            firstControl.Count.Should().Be(1);
            (
                await ObligationCalculationInstructions.CountDocumentsAsync(
                    FilterDefinition<ObligationCalculationInstruction>.Empty,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
                .Should()
                .Be(0);
            (
                await GetMongoDatabase()
                    .GetCollection<BsonDocument>(StateCollection)
                    .CountDocumentsAsync(
                        new BsonDocument("_id", JobName),
                        cancellationToken: TestContext.Current.CancellationToken
                    )
            )
                .Should()
                .Be(0);

            await secondWorker.StartAsync(TestContext.Current.CancellationToken);
            await secondTime.WaitForTimer(TimeSpan.FromMinutes(1));
            secondClock.Advance(TimeSpan.FromMinutes(1));
            await WaitForAsync(
                async () =>
                {
                    (
                        await ObligationCalculationInstructions.CountDocumentsAsync(
                            FilterDefinition<ObligationCalculationInstruction>.Empty,
                            cancellationToken: TestContext.Current.CancellationToken
                        )
                    )
                        .Should()
                        .Be(1);
                    (
                        await GetMongoDatabase()
                            .GetCollection<BsonDocument>(StateCollection)
                            .CountDocumentsAsync(
                                new BsonDocument("_id", JobName),
                                cancellationToken: TestContext.Current.CancellationToken
                            )
                    )
                        .Should()
                        .Be(1);
                },
                timeout: 5
            );
            secondControl.Count.Should().Be(1);
        }
        finally
        {
            await firstWorker.StopAsync(TestContext.Current.CancellationToken);
            await secondWorker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Schedule_WhenAnotherHostCrashes_ShouldRetryAfterLeaseExpiry()
    {
        await Reset();
        var time = new SchedulerTimeProvider(_clock);
        var owner = new ScheduledJobLeaseService(GetMongoApplicationDatabase(), time, JobName);
        (await owner.TryAcquire(TimeSpan.FromSeconds(61), TestContext.Current.CancellationToken)).Should().BeTrue();
        var control = new JobControl();
        await using var host = CreateHost(time, control, transactional: true);
        var worker = host.GetRequiredService<IHostedService>();
        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await time.WaitForTimer(TimeSpan.FromMinutes(1));
            _clock.Advance(TimeSpan.FromMinutes(1));
            await time.WaitForTimer(TimeSpan.FromSeconds(5));
            control.Count.Should().Be(0);
            _clock.Advance(TimeSpan.FromSeconds(5));
            await WaitForAsync(async () =>
            {
                (
                    await ObligationCalculationInstructions.CountDocumentsAsync(
                        FilterDefinition<ObligationCalculationInstruction>.Empty,
                        cancellationToken: TestContext.Current.CancellationToken
                    )
                )
                    .Should()
                    .Be(1);
            });
            control.Count.Should().Be(1);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
            await owner.Release(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Schedule_WhenAnotherHostAlreadyHandedOff_ShouldNotRepeatCompletedInstruction()
    {
        await Reset();
        var firstTime = new SchedulerTimeProvider(_clock);
        var firstControl = new JobControl();
        await using var first = CreateHost(firstTime, firstControl, transactional: true);
        var firstWorker = first.GetRequiredService<IHostedService>();
        var lateClock = new FakeTimeProvider(_clock.GetUtcNow());
        var lateTime = new SchedulerTimeProvider(lateClock);
        var lateControl = new JobControl();
        await using var late = CreateHost(lateTime, lateControl, transactional: true);
        var lateWorker = late.GetRequiredService<IHostedService>();
        try
        {
            await firstWorker.StartAsync(TestContext.Current.CancellationToken);
            await firstTime.WaitForTimer(TimeSpan.FromMinutes(1));
            _clock.Advance(TimeSpan.FromMinutes(1));
            await firstTime.WaitForTimer(TimeSpan.FromDays(1));
            await ObligationCalculationInstructions.UpdateOneAsync(
                FilterDefinition<ObligationCalculationInstruction>.Empty,
                Builders<ObligationCalculationInstruction>.Update.Set(
                    x => x.Status,
                    ObligationCalculationInstructionStatus.Completed
                ),
                cancellationToken: TestContext.Current.CancellationToken
            );
            await lateWorker.StartAsync(TestContext.Current.CancellationToken);
            await lateTime.WaitForTimer(TimeSpan.FromMinutes(1));
            lateClock.Advance(TimeSpan.FromMinutes(1));
            await lateTime.WaitForTimer(TimeSpan.FromDays(1));

            var instructions = await ObligationCalculationInstructions
                .Find(FilterDefinition<ObligationCalculationInstruction>.Empty)
                .ToListAsync(TestContext.Current.CancellationToken);
            instructions
                .Should()
                .ContainSingle()
                .Which.Status.Should()
                .Be(ObligationCalculationInstructionStatus.Completed);
            firstControl.Count.Should().Be(1);
            lateControl.Count.Should().Be(0);
        }
        finally
        {
            await firstWorker.StopAsync(TestContext.Current.CancellationToken);
            await lateWorker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Schedule_WhenHostsCompete_ShouldNotRepeatCompletedHandoff()
    {
        await Reset();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstClock = new FakeTimeProvider(_clock.GetUtcNow());
        var firstTime = new SchedulerTimeProvider(firstClock);
        var firstControl = new JobControl { Run = _ => finish.Task };
        await using var first = CreateHost(firstTime, firstControl, transactional: true);
        var firstWorker = first.GetRequiredService<IHostedService>();
        var secondClock = new FakeTimeProvider(_clock.GetUtcNow());
        var secondTime = new SchedulerTimeProvider(secondClock);
        var secondControl = new JobControl();
        await using var second = CreateHost(secondTime, secondControl, transactional: true);
        var secondWorker = second.GetRequiredService<IHostedService>();
        try
        {
            await firstWorker.StartAsync(TestContext.Current.CancellationToken);
            await secondWorker.StartAsync(TestContext.Current.CancellationToken);
            await firstTime.WaitForTimer(TimeSpan.FromMinutes(1));
            await secondTime.WaitForTimer(TimeSpan.FromMinutes(1));
            firstClock.Advance(TimeSpan.FromMinutes(1));
            await WaitForAsync(() =>
            {
                firstControl.Count.Should().Be(1);
                return Task.CompletedTask;
            });
            secondClock.Advance(TimeSpan.FromMinutes(1));
            await secondTime.WaitForTimer(TimeSpan.FromSeconds(5));
            finish.SetResult();
            await firstTime.WaitForTimer(TimeSpan.FromDays(1));
            await ObligationCalculationInstructions.UpdateOneAsync(
                FilterDefinition<ObligationCalculationInstruction>.Empty,
                Builders<ObligationCalculationInstruction>.Update.Set(
                    x => x.Status,
                    ObligationCalculationInstructionStatus.Completed
                ),
                cancellationToken: TestContext.Current.CancellationToken
            );
            secondClock.Advance(TimeSpan.FromSeconds(5));
            await secondTime.WaitForTimer(TimeSpan.FromDays(1) - TimeSpan.FromSeconds(5));

            var instructions = await ObligationCalculationInstructions
                .Find(FilterDefinition<ObligationCalculationInstruction>.Empty)
                .ToListAsync(TestContext.Current.CancellationToken);
            instructions
                .Should()
                .ContainSingle()
                .Which.Status.Should()
                .Be(ObligationCalculationInstructionStatus.Completed);
            secondControl.Count.Should().Be(0);
            var state = await GetMongoDatabase()
                .GetCollection<BsonDocument>(StateCollection)
                .Find(new BsonDocument("_id", JobName))
                .SingleAsync(TestContext.Current.CancellationToken);
            state["lastStartedAt"].ToUniversalTime().Should().Be(firstClock.GetUtcNow().UtcDateTime);
        }
        finally
        {
            finish.TrySetResult();
            await firstWorker.StopAsync(TestContext.Current.CancellationToken);
            await secondWorker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Schedule_WhenHandoffRepeatedlyFails_ShouldBackOffAndReduceLogSeverity()
    {
        await Reset();
        var logger = new RecordingLogger<ScheduledJobWorker>();
        var clock = new SchedulerTimeProvider(_clock);
        var retryTimers = Channel.CreateUnbounded<TimeSpan>();
        var capturedFailures = 0;
        clock.ObserveTimer = delay =>
        {
            var failures = logger.Entries.Count(x => x.Message.Contains("handoff") && x.Message.Contains("fail"));
            if (failures > capturedFailures)
            {
                capturedFailures = failures;
                retryTimers.Writer.TryWrite(delay);
            }
        };
        var control = new JobControl
        {
            Run = _ => Task.FromException(new InvalidOperationException("Mongo handoff failed")),
        };
        await using var host = CreateHost(clock, control, logger: logger);
        var worker = host.GetRequiredService<IHostedService>();
        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await clock.WaitForTimer(TimeSpan.FromMinutes(1));
            _clock.Advance(TimeSpan.FromMinutes(1));
            (int Min, int Max)[] expectedDelays = [(5, 5), (10, 12), (20, 24), (40, 48), (48, 59), (48, 59)];
            for (var i = 0; i < expectedDelays.Length; i++)
            {
                var (min, max) = expectedDelays[i];
                var delay = await retryTimers
                    .Reader.ReadAsync(TestContext.Current.CancellationToken)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(min));
                delay.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(max));
                if (i < expectedDelays.Length - 1)
                    _clock.Advance(delay);
            }

            var failures = logger
                .Entries.Where(x => x.Message.Contains("handoff") && x.Message.Contains("fail"))
                .ToArray();
            failures.Should().HaveCount(expectedDelays.Length);
            failures[0].Level.Should().Be(LogLevel.Error);
            failures.Skip(1).Should().OnlyContain(x => x.Level == LogLevel.Warning);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Schedule_WhenLeasesOverlap_ShouldCommitOneInstructionAndMarker()
    {
        await Reset();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        var releases = 0;
        var lease = Substitute.For<IScheduledJobLeaseService>();
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        lease.TryRenew(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        lease
            .Release(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref releases);
                return Task.CompletedTask;
            });
        Task WaitForBoth(CancellationToken token)
        {
            if (Interlocked.Increment(ref arrivals) == 2)
                arrived.TrySetResult();
            return proceed.Task.WaitAsync(token);
        }

        var initial = _clock.GetUtcNow();
        var firstClock = new FakeTimeProvider(initial);
        var secondClock = new FakeTimeProvider(initial);
        var firstTime = new SchedulerTimeProvider(firstClock);
        var secondTime = new SchedulerTimeProvider(secondClock);
        var firstControl = new JobControl { BeforeCreate = WaitForBoth };
        var secondControl = new JobControl { BeforeCreate = WaitForBoth };
        await using var first = CreateHost(firstTime, firstControl, leaseService: lease, transactional: true);
        await using var second = CreateHost(secondTime, secondControl, leaseService: lease, transactional: true);
        var firstWorker = first.GetRequiredService<IHostedService>();
        var secondWorker = second.GetRequiredService<IHostedService>();
        try
        {
            await firstWorker.StartAsync(TestContext.Current.CancellationToken);
            await secondWorker.StartAsync(TestContext.Current.CancellationToken);
            await firstTime.WaitForTimer(TimeSpan.FromMinutes(1));
            await secondTime.WaitForTimer(TimeSpan.FromMinutes(1));
            firstClock.Advance(TimeSpan.FromMinutes(1));
            secondClock.Advance(TimeSpan.FromMinutes(1));
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            proceed.SetResult();
            await WaitForAsync(
                async () =>
                {
                    releases.Should().BeGreaterThanOrEqualTo(2);
                    (
                        await ObligationCalculationInstructions.CountDocumentsAsync(
                            FilterDefinition<ObligationCalculationInstruction>.Empty,
                            cancellationToken: TestContext.Current.CancellationToken
                        )
                    )
                        .Should()
                        .Be(1);
                    (
                        await GetMongoDatabase()
                            .GetCollection<BsonDocument>(StateCollection)
                            .CountDocumentsAsync(
                                new BsonDocument("_id", JobName),
                                cancellationToken: TestContext.Current.CancellationToken
                            )
                    )
                        .Should()
                        .Be(1);
                },
                timeout: 15
            );
            firstClock.Advance(TimeSpan.FromMinutes(1));
            secondClock.Advance(TimeSpan.FromMinutes(1));
            await firstTime.WaitForTimerLongerThan(TimeSpan.FromHours(1));
            await secondTime.WaitForTimerLongerThan(TimeSpan.FromHours(1));
            (
                await ObligationCalculationInstructions.CountDocumentsAsync(
                    FilterDefinition<ObligationCalculationInstruction>.Empty,
                    cancellationToken: TestContext.Current.CancellationToken
                )
            )
                .Should()
                .Be(1);
        }
        finally
        {
            proceed.TrySetResult();
            await firstWorker.StopAsync(TestContext.Current.CancellationToken);
            await secondWorker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Schedule_WhenCommitResponseIsLost_ShouldNotCreateAnotherCompletedInstruction()
    {
        await Reset();
        var responseLost = false;
        IDbContext Decorate(IDbContext real)
        {
            var decorated = Substitute.For<IDbContext>();
            decorated
                .ExecuteTransaction(
                    Arg.Any<Func<IClientSessionHandle, CancellationToken, Task<bool>>>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>()
                )
                .Returns(async call =>
                {
                    var result = await real.ExecuteTransaction(
                        call.Arg<Func<IClientSessionHandle, CancellationToken, Task<bool>>>(),
                        call.Arg<string>(),
                        call.Arg<CancellationToken>()
                    );
                    if (!responseLost)
                    {
                        responseLost = true;
                        await ObligationCalculationInstructions.UpdateOneAsync(
                            FilterDefinition<ObligationCalculationInstruction>.Empty,
                            Builders<ObligationCalculationInstruction>.Update.Set(
                                x => x.Status,
                                ObligationCalculationInstructionStatus.Completed
                            ),
                            cancellationToken: TestContext.Current.CancellationToken
                        );
                        throw new TimeoutException("Commit response lost");
                    }

                    return result;
                });

            return decorated;
        }

        var time = new SchedulerTimeProvider(_clock);
        var control = new JobControl();
        await using var host = CreateHost(time, control, transactional: true, decorateContext: Decorate);
        var worker = host.GetRequiredService<IHostedService>();
        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await time.WaitForTimer(TimeSpan.FromMinutes(1));
            _clock.Advance(TimeSpan.FromMinutes(1));
            await time.WaitForTimer(TimeSpan.FromSeconds(5));
            _clock.Advance(TimeSpan.FromSeconds(5));
            await time.WaitForTimer(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(55));

            var instructions = await ObligationCalculationInstructions
                .Find(FilterDefinition<ObligationCalculationInstruction>.Empty)
                .ToListAsync(TestContext.Current.CancellationToken);
            instructions
                .Should()
                .ContainSingle()
                .Which.Status.Should()
                .Be(ObligationCalculationInstructionStatus.Completed);
            control.Count.Should().Be(1);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Schedule_WhenRetryReachesNextOccurrence_ShouldRunTheNextOccurrence()
    {
        await Reset();
        var lease = Substitute.For<IScheduledJobLeaseService>();
        var following = _clock.GetUtcNow().AddMinutes(2);
        lease
            .TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(_clock.GetUtcNow() >= following));
        lease.TryRenew(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        var time = new SchedulerTimeProvider(_clock);
        var control = new JobControl();
        await using var host = CreateHost(time, control, "* * * * *", leaseService: lease, transactional: true);
        var worker = host.GetRequiredService<IHostedService>();
        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await time.WaitForTimer(TimeSpan.FromMinutes(1));
            _clock.Advance(TimeSpan.FromMinutes(1));
            await time.WaitForTimer(TimeSpan.FromSeconds(5));
            _clock.Advance(TimeSpan.FromMinutes(1));
            await WaitForAsync(async () =>
            {
                var state = await GetMongoDatabase()
                    .GetCollection<BsonDocument>(StateCollection)
                    .Find(new BsonDocument("_id", JobName))
                    .SingleAsync(TestContext.Current.CancellationToken);
                state["lastStartedAt"].ToUniversalTime().Should().Be(following.UtcDateTime);
            });
            control.Count.Should().Be(1);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Schedule_WhenHostWakesLate_ShouldStillRunObservedOccurrence()
    {
        await Reset();
        var time = new SchedulerTimeProvider(_clock);
        var control = new JobControl();
        await using var host = CreateHost(time, control, "* * * * *");
        var worker = host.GetRequiredService<IHostedService>();
        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await time.WaitForTimer(TimeSpan.FromMinutes(1));
            _clock.Advance(TimeSpan.FromMinutes(2));
            await time.WaitForTimer(TimeSpan.FromMinutes(1));
            control.Count.Should().Be(1);
            var state = await GetMongoDatabase()
                .GetCollection<BsonDocument>(StateCollection)
                .Find(new BsonDocument("_id", JobName))
                .SingleAsync(TestContext.Current.CancellationToken);
            state["lastStartedAt"].ToUniversalTime().Should().Be(_clock.GetUtcNow().AddMinutes(-1).UtcDateTime);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task LongRunningJob_ShouldRenewLeaseAndExcludeOtherHostsUntilFinished()
    {
        await Reset();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new JobControl { Run = _ => finish.Task };
        var time = new SchedulerTimeProvider(_clock);
        await using var host = CreateHost(time, control, "* * * * *");
        var worker = host.GetRequiredService<IHostedService>();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await time.WaitForTimer(TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() =>
        {
            control.Count.Should().Be(1);
            return Task.CompletedTask;
        });
        var lease = await ReadLease();
        var initialExpiry = lease["expiresAt"].ToUniversalTime();

        _clock.Advance(TimeSpan.FromSeconds(40));
        await WaitForAsync(async () =>
            (await ReadLease())["expiresAt"].ToUniversalTime().Should().BeAfter(initialExpiry)
        );

        var secondTime = new SchedulerTimeProvider(_clock);
        await using var second = CreateHost(secondTime, control, "* * * * *");
        var secondWorker = second.GetRequiredService<IHostedService>();
        await secondWorker.StartAsync(TestContext.Current.CancellationToken);
        await secondTime.WaitForTimer(TimeSpan.FromSeconds(20));
        _clock.Advance(TimeSpan.FromSeconds(20));
        await secondTime.WaitForTimer(TimeSpan.FromSeconds(5));

        control.Count.Should().Be(1);
        finish.SetResult();
        await time.WaitForTimer(TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() =>
        {
            control.Count.Should().Be(2);
            return Task.CompletedTask;
        });

        await worker.StopAsync(TestContext.Current.CancellationToken);
        await secondWorker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FailedJob_ShouldReleaseLeaseAndRunAtNextOccurrence()
    {
        await Reset();
        var control = new JobControl { Run = _ => Task.FromException(new InvalidOperationException("Job failed")) };
        var time = new SchedulerTimeProvider(_clock);
        await using var host = CreateHost(time, control, "* * * * *");
        var worker = host.GetRequiredService<IHostedService>();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await time.WaitForTimer(TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await time.WaitForTimer(TimeSpan.FromSeconds(5));

        control.Count.Should().Be(1);
        (await ReadLease()).Contains("owner").Should().BeFalse();

        _clock.Advance(TimeSpan.FromMinutes(1));
        await time.WaitForTimer(TimeSpan.FromSeconds(5));

        control.Count.Should().Be(2);
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LostLease_ShouldCancelAndDrainJobBeforeStartingAnotherOccurrence()
    {
        await Reset();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new JobControl
        {
            Run = async token =>
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                await finish.Task;
            },
        };
        var time = new SchedulerTimeProvider(_clock);
        await using var host = CreateHost(time, control, "* * * * *");
        var worker = host.GetRequiredService<IHostedService>();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await time.WaitForTimer(TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() =>
        {
            control.Count.Should().Be(1);
            return Task.CompletedTask;
        });
        await GetMongoDatabase()
            .GetCollection<BsonDocument>(LeaseCollection)
            .UpdateOneAsync(
                new BsonDocument("_id", JobName),
                new BsonDocument("$set", new BsonDocument("owner", "another-host")),
                cancellationToken: TestContext.Current.CancellationToken
            );

        _clock.Advance(TimeSpan.FromSeconds(20));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromMinutes(2));

        control.Count.Should().Be(1);
        (await ReadLease())["owner"].AsString.Should().Be("another-host");
        var stopping = worker.StopAsync(TestContext.Current.CancellationToken);
        finish.SetResult();
        await stopping;
        (await ReadLease())["owner"].AsString.Should().Be("another-host");
    }

    [Fact]
    public async Task SuccessfulHandoff_WhenCleanupFails_ShouldLogWarningWithoutRetrying()
    {
        await Reset();
        var failure = new InvalidOperationException("Scope cleanup failed");
        var control = new JobControl { Dispose = () => Task.FromException(failure) };
        var time = new SchedulerTimeProvider(_clock);
        var logger = new RecordingLogger<ScheduledJobWorker>();
        await using var host = CreateHost(time, control, logger: logger);
        var worker = host.GetRequiredService<IHostedService>();
        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await time.WaitForTimer(TimeSpan.FromMinutes(1));
            _clock.Advance(TimeSpan.FromMinutes(1));
            await time.WaitForTimer(TimeSpan.FromDays(1));

            control.Count.Should().Be(1);
            var state = await GetMongoDatabase()
                .GetCollection<BsonDocument>(StateCollection)
                .Find(new BsonDocument("_id", JobName))
                .SingleAsync(TestContext.Current.CancellationToken);
            state["lastStartedAt"].ToUniversalTime().Should().Be(_clock.GetUtcNow().UtcDateTime);
            logger
                .Entries.Should()
                .ContainSingle(x => x.Level == LogLevel.Warning && x.Message.Contains("cleanup failed"));
            logger.Entries.Should().NotContain(x => x.Level == LogLevel.Error);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task FailedJob_WhenScopeDisposalAlsoFails_ShouldReportBothErrorsAndReleaseLease()
    {
        await Reset();
        var jobFailure = new InvalidOperationException("Job execution failed");
        var cleanupFailure = new InvalidOperationException("Cancellation cleanup failed");
        var control = new JobControl
        {
            Run = _ => Task.FromException(jobFailure),
            Dispose = () => Task.FromException(cleanupFailure),
        };
        var time = new SchedulerTimeProvider(_clock);
        var logger = new RecordingLogger<ScheduledJobWorker>();
        await using var host = CreateHost(time, control, logger: logger);
        var worker = host.GetRequiredService<IHostedService>();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await time.WaitForTimer(TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await time.WaitForTimer(TimeSpan.FromSeconds(5));

        var reported = logger.Entries.Single(x => x.Exception is AggregateException);
        ((AggregateException)reported.Exception!)
            .Flatten()
            .InnerExceptions.Should()
            .Contain(jobFailure)
            .And.Contain(cleanupFailure);
        (await ReadLease()).Contains("owner").Should().BeFalse();
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Schedule_WhenStateHasAdditionalFields_ShouldRunAndPreserveThem()
    {
        await Reset();
        await GetMongoDatabase()
            .GetCollection<BsonDocument>(StateCollection)
            .InsertOneAsync(
                new BsonDocument
                {
                    { "_id", JobName },
                    { "lastStartedAt", _clock.GetUtcNow().AddDays(-1).UtcDateTime },
                    { "futureField", "preserved" },
                },
                cancellationToken: TestContext.Current.CancellationToken
            );
        var control = new JobControl();
        var time = new SchedulerTimeProvider(_clock);
        await using var host = CreateHost(time, control);
        var worker = host.GetRequiredService<IHostedService>();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await time.WaitForTimer(TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await time.WaitForTimer(TimeSpan.FromDays(1));

        control.Count.Should().Be(1);
        var state = await GetMongoDatabase()
            .GetCollection<BsonDocument>(StateCollection)
            .Find(new BsonDocument("_id", JobName))
            .SingleAsync(TestContext.Current.CancellationToken);
        state["futureField"].AsString.Should().Be("preserved");
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Schedule_WhenThereIsNoFutureOccurrence_ShouldLogWhyItStopped()
    {
        var logger = new RecordingLogger<ScheduledJobWorker>();
        var control = new JobControl();
        await using var host = CreateHost(new SchedulerTimeProvider(_clock), control, "0 0 30 2 *", logger);
        var worker = (BackgroundService)host.GetRequiredService<IHostedService>();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        logger
            .Entries.Should()
            .ContainSingle(x => x.Level == LogLevel.Warning && x.Message.Contains("no future cron occurrence"));
        control.Count.Should().Be(0);
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StalledAcquisition_WhenItEventuallySucceeds_ShouldReleaseWithoutRunningJob()
    {
        var lease = Substitute.For<IScheduledJobLeaseService>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lease
            .TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().Register(() => cancelled.TrySetResult());
                started.TrySetResult();

                return acquired.Task;
            });
        var control = new JobControl();
        var time = new SchedulerTimeProvider(_clock);
        await using var host = CreateHost(time, control, "* * * * *", leaseService: lease);
        var worker = host.GetRequiredService<IHostedService>();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await time.WaitForTimer(TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(11));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        acquired.SetResult(true);
        await time.WaitForTimer(TimeSpan.FromSeconds(5));

        control.Count.Should().Be(0);
        await lease.Received(1).Release(Arg.Any<CancellationToken>());
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StalledRenewal_ShouldCancelJobAndDrainBothBeforeReleasingLease()
    {
        await Reset();
        var lease = Substitute.For<IScheduledJobLeaseService>();
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        var renewalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lease
            .TryRenew(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                renewalStarted.TrySetResult();

                return renewed.Task;
            });
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new JobControl
        {
            Run = token =>
            {
                token.Register(() => cancelled.TrySetResult());

                return finished.Task;
            },
        };
        var time = new SchedulerTimeProvider(_clock);
        await using var host = CreateHost(time, control, "* * * * *", leaseService: lease);
        var worker = host.GetRequiredService<IHostedService>();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await time.WaitForTimer(TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForAsync(() =>
        {
            control.Count.Should().Be(1);
            return Task.CompletedTask;
        });
        _clock.Advance(TimeSpan.FromSeconds(20));
        await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(11));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await lease.DidNotReceive().Release(Arg.Any<CancellationToken>());

        finished.SetResult();
        await lease.DidNotReceive().Release(Arg.Any<CancellationToken>());
        renewed.SetResult(true);
        await time.WaitForTimer(TimeSpan.FromSeconds(5));

        await lease.Received(1).Release(Arg.Any<CancellationToken>());
        control.Count.Should().Be(1);
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StalledRelease_ShouldRequestCancellationAndAllowWorkerToFinish()
    {
        await Reset();
        var lease = Substitute.For<IScheduledJobLeaseService>();
        lease.TryAcquire(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        var releaseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lease
            .Release(Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var token = call.Arg<CancellationToken>();
                token.Register(() => cancelled.TrySetResult());
                releaseStarted.TrySetResult();

                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        var time = new SchedulerTimeProvider(_clock);
        var control = new JobControl();
        var logger = new RecordingLogger<ScheduledJobWorker>();
        await using var host = CreateHost(time, control, "* * * * *", logger, lease);
        var worker = host.GetRequiredService<IHostedService>();
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await time.WaitForTimer(TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await releaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromSeconds(11));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await time.WaitForTimer(TimeSpan.FromSeconds(49));

        control.Count.Should().Be(1);
        logger.Messages.Should().Contain(x => x.Contains("lease could not be released"));
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    private static ServiceProvider CreateHost(
        SchedulerTimeProvider time,
        JobControl control,
        string cron = "0 1 * * *",
        ILogger<ScheduledJobWorker>? logger = null,
        IScheduledJobLeaseService? leaseService = null,
        bool transactional = false,
        Func<IDbContext, IDbContext>? decorateContext = null
    ) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(GetMongoApplicationDatabase())
            .AddSingleton<TimeProvider>(time)
            .AddSingleton(control)
            .AddOptions()
            .AddScoped<MongoDbContext>()
            .AddScoped<IDbContext>(x =>
                decorateContext?.Invoke(x.GetRequiredService<MongoDbContext>())
                ?? x.GetRequiredService<MongoDbContext>()
            )
            .AddTransient<IObligationCalculationInstructionService, ObligationCalculationInstructionService>()
            .AddScoped<TestJob>()
            .AddScoped<TestTransactionalJob>()
            .AddSingleton<IHostedService>(provider => new ScheduledJobWorker(
                JobName,
                CronExpression.Parse(cron),
                transactional ? typeof(TestTransactionalJob) : typeof(TestJob),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IMongoDatabase>(),
                leaseService
                    ?? new ScheduledJobLeaseService(provider.GetRequiredService<IMongoDatabase>(), time, JobName),
                time,
                logger ?? NullLogger<ScheduledJobWorker>.Instance
            ))
            .BuildServiceProvider();

    private static async Task Reset()
    {
        var filter = new BsonDocument(
            "_id",
            new BsonDocument("$in", new BsonArray { JobName, "ObligationCalculation" })
        );
        await GetMongoDatabase()
            .GetCollection<BsonDocument>(LeaseCollection)
            .DeleteManyAsync(filter, TestContext.Current.CancellationToken);
        await GetMongoDatabase()
            .GetCollection<BsonDocument>(StateCollection)
            .DeleteManyAsync(filter, TestContext.Current.CancellationToken);
    }

    private static Task<BsonDocument> ReadLease() =>
        GetMongoDatabase()
            .GetCollection<BsonDocument>(LeaseCollection)
            .Find(new BsonDocument("_id", JobName))
            .SingleAsync(TestContext.Current.CancellationToken);

    private sealed class JobControl
    {
        public int Count;
        public Func<CancellationToken, Task> Run { get; init; } = _ => Task.CompletedTask;
        public Func<CancellationToken, Task> BeforeCreate { get; init; } = _ => Task.CompletedTask;
        public Func<Task> Dispose { get; init; } = () => Task.CompletedTask;
    }

    private sealed class TestJob(JobControl control) : IScheduledJob, IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(control.Dispose());

        public Task Run(IClientSessionHandle session, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref control.Count);

            return control.Run(cancellationToken);
        }
    }

    private sealed class TestTransactionalJob(JobControl control, IObligationCalculationInstructionService instructions)
        : IScheduledJob
    {
        public async Task Run(IClientSessionHandle session, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref control.Count);
            await control.BeforeCreate(cancellationToken);
            await instructions.Create(session, 2026, null, cancellationToken);
            await control.Run(cancellationToken);
        }
    }

    private sealed class SchedulerTimeProvider(FakeTimeProvider clock) : TimeProvider
    {
        private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();
        private readonly TaskCompletionSource _retryTimer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RetryTimer => _retryTimer.Task;
        public Action<TimeSpan>? ObserveTimer { get; set; }

        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = clock.CreateTimer(callback, state, dueTime, period);
            _timers.Writer.TryWrite(dueTime);
            ObserveTimer?.Invoke(dueTime);
            if (dueTime == TimeSpan.FromSeconds(5))
                _retryTimer.TrySetResult();

            return timer;
        }

        public async Task WaitForTimerLongerThan(TimeSpan minimum)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (await _timers.Reader.ReadAsync(timeout.Token) <= minimum)
            {
                // Ignore lease and retry timers.
            }
        }

        public async Task WaitForTimer(TimeSpan dueTime)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (await _timers.Reader.ReadAsync(timeout.Token) != dueTime)
            {
                // Ignore unrelated lease-renewal timers.
            }
        }
    }
}
