using AwesomeAssertions;
using Defra.WasteObligations.AuditEvents;
using Defra.WasteObligations.AuditEvents.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using NSubstitute;

namespace Defra.WasteObligations.Api.IntegrationTests.AuditEvents;

public class AuditEventLeaseServiceTests : IntegrationTestBase
{
    private const string Analytics = "analytics-lease-test";

    [Fact]
    public async Task TryAcquire_ShouldCreateAndOwnLease()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var subject = CreateSubject(timeProvider);

        var result = await subject.TryAcquire(
            Analytics,
            TimeSpan.FromSeconds(60),
            TestContext.Current.CancellationToken
        );

        result.Should().BeTrue();
        var lease = await AuditEventDispatchLeases
            .Find(x => x.Id == Analytics)
            .SingleAsync(TestContext.Current.CancellationToken);
        lease.Owner.Should().NotBeNullOrWhiteSpace();
        lease.CreatedAt.Should().Be(timeProvider.GetUtcNow().UtcDateTime);
        lease.UpdatedAt.Should().Be(timeProvider.GetUtcNow().UtcDateTime);
        lease.ExpiresAt.Should().Be(timeProvider.GetUtcNow().AddSeconds(60).UtcDateTime);
    }

    [Fact]
    public async Task TryAcquire_WhenLeaseIsUnexpired_ShouldReturnFalseForAnotherInstance()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var firstInstance = CreateSubject(timeProvider);
        var secondInstance = CreateSubject(timeProvider);

        await firstInstance.TryAcquire(Analytics, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var result = await secondInstance.TryAcquire(
            Analytics,
            TimeSpan.FromSeconds(60),
            TestContext.Current.CancellationToken
        );

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryAcquire_WhenLeaseHasExpired_ShouldAllowAnotherInstanceToAcquire()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var firstInstance = CreateSubject(timeProvider);
        var secondInstance = CreateSubject(timeProvider);
        await firstInstance.TryAcquire(Analytics, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        timeProvider.Advance(TimeSpan.FromSeconds(61));

        var result = await secondInstance.TryAcquire(
            Analytics,
            TimeSpan.FromSeconds(60),
            TestContext.Current.CancellationToken
        );

        result.Should().BeTrue();
    }

    [Fact]
    public async Task TryRenew_ShouldOnlyExtendLeaseForOwner()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var owner = CreateSubject(timeProvider);
        var nonOwner = CreateSubject(timeProvider);
        await owner.TryAcquire(Analytics, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(10));

        var nonOwnerResult = await nonOwner.TryRenew(
            Analytics,
            TimeSpan.FromSeconds(60),
            TestContext.Current.CancellationToken
        );
        var ownerResult = await owner.TryRenew(
            Analytics,
            TimeSpan.FromSeconds(60),
            TestContext.Current.CancellationToken
        );

        nonOwnerResult.Should().BeFalse();
        ownerResult.Should().BeTrue();
        var lease = await AuditEventDispatchLeases
            .Find(x => x.Id == Analytics)
            .SingleAsync(TestContext.Current.CancellationToken);
        lease.ExpiresAt.Should().Be(timeProvider.GetUtcNow().AddSeconds(60).UtcDateTime);
    }

    [Fact]
    public async Task Release_ShouldClearOwnerSetLastSentAtAndAllowReacquisition()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var owner = CreateSubject(timeProvider);
        var nextOwner = CreateSubject(timeProvider);
        await owner.TryAcquire(Analytics, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        await owner.Release(Analytics, TestContext.Current.CancellationToken);
        var result = await nextOwner.TryAcquire(
            Analytics,
            TimeSpan.FromSeconds(60),
            TestContext.Current.CancellationToken
        );

        result.Should().BeTrue();
        var lease = await AuditEventDispatchLeases
            .Find(x => x.Id == Analytics)
            .SingleAsync(TestContext.Current.CancellationToken);
        lease.Owner.Should().NotBeNullOrWhiteSpace();
        lease.LastSentAt.Should().Be(timeProvider.GetUtcNow().UtcDateTime);
    }

    private static AuditEventLeaseService CreateSubject(TimeProvider timeProvider) =>
        new(
            new AuditEventDbContext(GetMongoApplicationDatabase()),
            timeProvider,
            Substitute.For<ILogger<AuditEventLeaseService>>()
        );
}
