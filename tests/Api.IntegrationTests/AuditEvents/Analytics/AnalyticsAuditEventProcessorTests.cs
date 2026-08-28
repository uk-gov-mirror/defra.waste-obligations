using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.AuditEvents;
using Defra.WasteObligations.AuditEvents.Analytics;
using Defra.WasteObligations.AuditEvents.Data;
using Defra.WasteObligations.AuditEvents.Entities;
using Defra.WasteObligations.AuditEvents.Metrics;
using Defra.WasteObligations.Testing;
using Defra.WasteObligations.Testing.Fixtures.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using NSubstitute;

namespace Defra.WasteObligations.Api.IntegrationTests.AuditEvents.Analytics;

public class AnalyticsAuditEventProcessorTests : IntegrationTestBase
{
    private const string Analytics = "analytics-test";

    [Fact]
    public async Task Start_WhenAuditEventIsUnsent_ShouldSendAndMarkDispatched()
    {
        var sentAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(sentAt);
        var sender = new RecordingAnalyticsEventSender();
        var logger = new RecordingLogger<AnalyticsAuditEventProcessor>();
        var auditEventMetrics = Substitute.For<IAuditEventMetrics>();
        var auditEvent = CreateAuditEvent("event-1", 1) with { TraceId = TraceId };
        await AuditEvents.InsertOneAsync(auditEvent, cancellationToken: TestContext.Current.CancellationToken);
        var subject = CreateSubject(
            GetMongoDatabase(),
            timeProvider,
            sender,
            auditEventMetrics: auditEventMetrics,
            logger: logger
        );

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await sender.WaitForSend(TestContext.Current.CancellationToken);

        sender.SentEvents.Single().Should().BeEquivalentTo(auditEvent.ToAnalyticsEvent());
        await AsyncWaiter.WaitForAsync(
            async () =>
            {
                var result = await AuditEvents
                    .Find(x => x.EventId == auditEvent.EventId)
                    .SingleAsync(TestContext.Current.CancellationToken);
                result
                    .Dispatches[Analytics]
                    .Should()
                    .BeEquivalentTo(
                        new AuditEventDispatch
                        {
                            Status = AuditEventDispatchStatus.Dispatched,
                            Date = sentAt.UtcDateTime,
                            AttemptCount = 1,
                        }
                    );
                logger.Messages.Should().Contain(x => x.Contains(auditEvent.EventId) && x.Contains(TraceId));
            },
            timeout: 5,
            delay: TimeSpan.FromMilliseconds(50)
        );
        await subject.StopAsync(TestContext.Current.CancellationToken);

        auditEventMetrics.Received().DispatchPollStarted(Analytics);
        auditEventMetrics.Received().DispatchLeaseAcquired(Analytics);
        auditEventMetrics.Received().DispatchPollCompleted(Analytics, Arg.Any<double>());
    }

    [Fact]
    public async Task Start_WhenAuditEventAlreadyDispatched_ShouldNotSend()
    {
        var auditEvent = CreateAuditEvent(
            "event-1",
            1,
            new Dictionary<string, AuditEventDispatch>
            {
                [Analytics] = new()
                {
                    Status = AuditEventDispatchStatus.Dispatched,
                    Date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                },
            }
        );
        await AuditEvents.InsertOneAsync(auditEvent, cancellationToken: TestContext.Current.CancellationToken);
        var sender = new RecordingAnalyticsEventSender();
        var subject = CreateSubject(GetMongoDatabase(), new FakeTimeProvider(), sender);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        sender.SentEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_WhenAuditEventPreviouslyFailed_ShouldLogRetryAndMarkDispatched()
    {
        var sentAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(sentAt);
        var sender = new RecordingAnalyticsEventSender();
        var logger = new RecordingLogger<AnalyticsAuditEventProcessor>();
        var auditEvent = CreateAuditEvent(
            "event-1",
            1,
            new Dictionary<string, AuditEventDispatch>
            {
                [Analytics] = new()
                {
                    Status = AuditEventDispatchStatus.Failed,
                    Date = sentAt.UtcDateTime.AddMinutes(-2),
                    Message = "Sender failed",
                    AttemptCount = 1,
                    NextAttemptAt = sentAt.UtcDateTime.AddMinutes(-1),
                },
            }
        ) with
        {
            TraceId = TraceId,
        };
        await AuditEvents.InsertOneAsync(auditEvent, cancellationToken: TestContext.Current.CancellationToken);
        var subject = CreateSubject(GetMongoDatabase(), timeProvider, sender, logger: logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await sender.WaitForSend(TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        logger
            .Entries.Should()
            .Contain(x =>
                x.Level == LogLevel.Information
                && x.Message.Contains("Retrying failed audit event event-1 for analytics-test")
                && x.Message.Contains(TraceId)
            );
        await AsyncWaiter.WaitForAsync(
            async () =>
            {
                var result = await AuditEvents
                    .Find(x => x.EventId == auditEvent.EventId)
                    .SingleAsync(TestContext.Current.CancellationToken);
                result.Dispatches[Analytics].Status.Should().Be(AuditEventDispatchStatus.Dispatched);
            },
            timeout: 5,
            delay: TimeSpan.FromMilliseconds(50)
        );
    }

    [Fact]
    public async Task Start_WhenSenderThrows_ShouldContinueUntilStopped()
    {
        var auditEvent = CreateAuditEvent("event-1", 1);
        await AuditEvents.InsertOneAsync(auditEvent, cancellationToken: TestContext.Current.CancellationToken);
        var sender = new RecordingAnalyticsEventSender
        {
            OnSend = (x, cancellationToken) =>
            {
                if (x.EventId == auditEvent.EventId)
                    throw new InvalidOperationException("Sender failed");

                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
        };
        var logger = new RecordingLogger<AnalyticsAuditEventProcessor>();
        var subject = CreateSubject(GetMongoDatabase(), new FakeTimeProvider(), sender, logger: logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await sender.WaitForSend(TestContext.Current.CancellationToken);
        var act = () => subject.StopAsync(TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        sender.SentEvents.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Start_WhenSenderThrows_ShouldMarkFailedAndContinueProcessingRemainingEvents()
    {
        const string message = "Sender failed";

        var failedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var firstAuditEvent = CreateAuditEvent("event-1", 1);
        var secondAuditEvent = CreateAuditEvent("event-2", 2);
        await AuditEvents.InsertManyAsync(
            [firstAuditEvent, secondAuditEvent],
            cancellationToken: TestContext.Current.CancellationToken
        );
        var sender = new RecordingAnalyticsEventSender
        {
            OnSend = (x, _) =>
            {
                if (x.EventId == firstAuditEvent.EventId)
                    throw new InvalidOperationException(message);

                return Task.CompletedTask;
            },
        };
        var logger = new RecordingLogger<AnalyticsAuditEventProcessor>();
        var subject = CreateSubject(GetMongoDatabase(), new FakeTimeProvider(failedAt), sender, logger: logger);

        await subject.StartAsync(TestContext.Current.CancellationToken);

        await AsyncWaiter.WaitForAsync(
            () =>
            {
                sender
                    .SentEvents.Select(x => x.EventId)
                    .Should()
                    .Contain(firstAuditEvent.EventId)
                    .And.Contain(secondAuditEvent.EventId);

                return Task.CompletedTask;
            },
            timeout: 5,
            delay: TimeSpan.FromMilliseconds(50)
        );
        await AsyncWaiter.WaitForAsync(
            async () =>
            {
                var results = await AuditEvents.Find(_ => true).ToListAsync(TestContext.Current.CancellationToken);
                results
                    .Single(x => x.EventId == firstAuditEvent.EventId)
                    .Dispatches[Analytics]
                    .Should()
                    .BeEquivalentTo(
                        new AuditEventDispatch
                        {
                            Status = AuditEventDispatchStatus.Failed,
                            Date = failedAt.UtcDateTime,
                            Message = message,
                            AttemptCount = 1,
                            NextAttemptAt = failedAt.UtcDateTime.AddMinutes(1),
                        }
                    );
                results
                    .Single(x => x.EventId == secondAuditEvent.EventId)
                    .Dispatches[Analytics]
                    .Status.Should()
                    .Be(AuditEventDispatchStatus.Dispatched);
                logger.Messages.Should().ContainSingle(x => x == "Processed 1 audit events for analytics-test");
            },
            timeout: 5,
            delay: TimeSpan.FromMilliseconds(50)
        );
        await subject.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Start_WhenProcessingIsDisabled_ShouldLogAndNotSend()
    {
        var auditEvent = CreateAuditEvent("event-1", 1);
        await AuditEvents.InsertOneAsync(auditEvent, cancellationToken: TestContext.Current.CancellationToken);
        var sender = new RecordingAnalyticsEventSender();
        var logger = new RecordingLogger<AnalyticsAuditEventProcessor>();
        var auditEventMetrics = Substitute.For<IAuditEventMetrics>();
        var subject = CreateSubject(
            GetMongoDatabase(),
            new FakeTimeProvider(),
            sender,
            auditEventMetrics: auditEventMetrics,
            processingEnabled: false,
            logger: logger
        );

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        sender.SentEvents.Should().BeEmpty();
        logger.Messages.Should().ContainSingle("Analytics audit event processing is off");
        auditEventMetrics.DidNotReceive().DispatchPollStarted(Arg.Any<string>());
    }

    [Fact]
    public async Task Start_WhenLeaseRenewalFails_ShouldStopProcessingRemainingEvents()
    {
        const string anotherInstance = "another-instance";

        var firstAuditEvent = CreateAuditEvent("event-1", 1);
        var secondAuditEvent = CreateAuditEvent("event-2", 2);
        var leaseOwnerChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await AuditEvents.InsertManyAsync(
            [firstAuditEvent, secondAuditEvent],
            cancellationToken: TestContext.Current.CancellationToken
        );

        var sender = new RecordingAnalyticsEventSender
        {
            OnSend = async (_, cancellationToken) =>
            {
                await AuditEventDispatchLeases.UpdateOneAsync(
                    x => x.Id == Analytics,
                    Builders<AuditEventDispatchLease>.Update.Set(x => x.Owner, anotherInstance),
                    cancellationToken: cancellationToken
                );
                leaseOwnerChanged.TrySetResult();
            },
        };
        var logger = new RecordingLogger<AnalyticsAuditEventProcessor>();
        var auditEventMetrics = Substitute.For<IAuditEventMetrics>();
        var subject = CreateSubject(
            GetMongoDatabase(),
            new FakeTimeProvider(),
            sender,
            auditEventMetrics: auditEventMetrics,
            logger: logger
        );

        await subject.StartAsync(TestContext.Current.CancellationToken);
        await sender.WaitForSend(TestContext.Current.CancellationToken);
        await leaseOwnerChanged.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await AsyncWaiter.WaitForAsync(
            async () =>
            {
                var firstResult = await AuditEvents
                    .Find(x => x.EventId == firstAuditEvent.EventId)
                    .SingleAsync(TestContext.Current.CancellationToken);
                firstResult.Dispatches.Should().ContainKey(Analytics);
            },
            timeout: 5,
            delay: TimeSpan.FromMilliseconds(50)
        );
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        await subject.StopAsync(TestContext.Current.CancellationToken);

        sender.SentEvents.Should().ContainSingle();
        sender.SentEvents.Single().EventId.Should().Be(firstAuditEvent.EventId);
        logger
            .Entries.Should()
            .Contain(x =>
                x.Level == LogLevel.Error
                && x.Message.Contains("Stopped analytics audit event processing because lease renewal failed")
            );
        auditEventMetrics.Received().DispatchLeaseRenewalFailed(Analytics);
        var secondResult = await AuditEvents
            .Find(x => x.EventId == secondAuditEvent.EventId)
            .SingleAsync(TestContext.Current.CancellationToken);
        secondResult.Dispatches.Should().NotContainKey(Analytics);
    }

    private static AnalyticsAuditEventProcessor CreateSubject(
        IMongoDatabase database,
        TimeProvider timeProvider,
        IAnalyticsEventSender sender,
        IAuditEventMetrics? auditEventMetrics = null,
        bool processingEnabled = true,
        ILogger<AnalyticsAuditEventProcessor>? logger = null
    )
    {
        auditEventMetrics ??= Substitute.For<IAuditEventMetrics>();

        var services = new ServiceCollection();
        services.AddSingleton(database);
        services.AddSingleton(timeProvider);
        services.AddSingleton(sender);
        services.AddSingleton(auditEventMetrics);
        services.AddLogging();
        services.AddScoped<IAuditEventDbContext, AuditEventDbContext>();
        services.AddScoped<AuditEventLeaseService>();
        services.AddScoped<AuditEventDispatchService>();
        var serviceProvider = services.BuildServiceProvider();

        return new AnalyticsAuditEventProcessor(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(
                new AnalyticsAuditEventProcessorOptions
                {
                    ProcessName = Analytics,
                    TopicArn = "arn:aws:sns:eu-west-2:000000000000:waste_obligations_analytics_events",
                    ProcessingEnabled = processingEnabled,
                    BatchSize = 25,
                    PollIntervalSeconds = 0,
                    PollJitterSeconds = 0,
                    LeaseDurationSeconds = 60,
                }
            ),
            auditEventMetrics,
            logger ?? Substitute.For<ILogger<AnalyticsAuditEventProcessor>>()
        );
    }

    private static AuditEvent CreateAuditEvent(
        string eventId,
        long sequence,
        Dictionary<string, AuditEventDispatch>? dispatches = null
    ) => AuditEventFixture.ComplianceDeclaration(eventId, sequence).With(x => x.Dispatches, dispatches ?? []).Create();

    private sealed class RecordingAnalyticsEventSender : IAnalyticsEventSender
    {
        private readonly TaskCompletionSource _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<AnalyticsEvent> SentEvents { get; } = [];

        public Func<AnalyticsEvent, CancellationToken, Task>? OnSend { get; set; }

        public async Task WaitForSend(CancellationToken cancellationToken) =>
            await _sent.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        public async Task Send(AnalyticsEvent analyticsEvent, CancellationToken cancellationToken)
        {
            SentEvents.Add(analyticsEvent);
            _sent.TrySetResult();

            if (OnSend is not null)
            {
                await OnSend(analyticsEvent, cancellationToken);
            }
        }
    }
}
