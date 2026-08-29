using AwesomeAssertions;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Services.OrganisationObligations;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;

namespace Defra.WasteObligations.Api.IntegrationTests.Services.OrganisationObligations;

public class OrganisationObligationHydrationLeaseServiceTests : IntegrationTestBase
{
    [Fact]
    public async Task TryAcquire_ShouldCreateAndOwnLease()
    {
        var timeProvider = CreateTimeProvider();
        var subject = CreateSubject(timeProvider);

        var result = await subject.TryAcquire(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        var lease = await OrganisationWorkerLeases
            .Find(x => x.Id == BackgroundWorkerLease.OrganisationObligationHydrationLeaseId)
            .SingleAsync(TestContext.Current.CancellationToken);
        lease.Owner.Should().NotBeNullOrWhiteSpace();
        lease.CreatedAt.Should().Be(timeProvider.GetUtcNow().UtcDateTime);
        lease.UpdatedAt.Should().Be(timeProvider.GetUtcNow().UtcDateTime);
        lease.ExpiresAt.Should().Be(timeProvider.GetUtcNow().AddSeconds(60).UtcDateTime);
    }

    [Fact]
    public async Task TryAcquire_WhenLeaseIsUnexpired_ShouldReturnFalseForAnotherInstance()
    {
        var timeProvider = CreateTimeProvider();
        var firstInstance = CreateSubject(timeProvider);
        var secondInstance = CreateSubject(timeProvider);
        await firstInstance.TryAcquire(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        var result = await secondInstance.TryAcquire(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryAcquire_WhenLeaseHasExpired_ShouldAllowAnotherInstanceToAcquire()
    {
        var timeProvider = CreateTimeProvider();
        var firstInstance = CreateSubject(timeProvider);
        var secondInstance = CreateSubject(timeProvider);
        await firstInstance.TryAcquire(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(61));

        var result = await secondInstance.TryAcquire(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task TryRenew_ShouldOnlyExtendLeaseForOwner()
    {
        var timeProvider = CreateTimeProvider();
        var owner = CreateSubject(timeProvider);
        var nonOwner = CreateSubject(timeProvider);
        await owner.TryAcquire(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(10));

        var nonOwnerResult = await nonOwner.TryRenew(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var ownerResult = await owner.TryRenew(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        nonOwnerResult.Should().BeFalse();
        ownerResult.Should().BeTrue();
        var lease = await OrganisationWorkerLeases
            .Find(x => x.Id == BackgroundWorkerLease.OrganisationObligationHydrationLeaseId)
            .SingleAsync(TestContext.Current.CancellationToken);
        lease.ExpiresAt.Should().Be(timeProvider.GetUtcNow().AddSeconds(60).UtcDateTime);
    }

    [Fact]
    public async Task Release_ShouldClearOwnerRecordReleaseAndAllowReacquisition()
    {
        var timeProvider = CreateTimeProvider();
        var owner = CreateSubject(timeProvider);
        var nextOwner = CreateSubject(timeProvider);
        await owner.TryAcquire(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        await owner.Release(TestContext.Current.CancellationToken);
        var result = await nextOwner.TryAcquire(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        var lease = await OrganisationWorkerLeases
            .Find(x => x.Id == BackgroundWorkerLease.OrganisationObligationHydrationLeaseId)
            .SingleAsync(TestContext.Current.CancellationToken);
        lease.Owner.Should().NotBeNullOrWhiteSpace();
        lease.LastReleasedAt.Should().Be(timeProvider.GetUtcNow().UtcDateTime);
    }

    private static FakeTimeProvider CreateTimeProvider() =>
        new(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));

    private static OrganisationObligationHydrationLeaseService CreateSubject(TimeProvider timeProvider) =>
        new(
            GetMongoApplicationDatabase(),
            timeProvider,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrganisationObligationHydrationLeaseService>.Instance
        );
}
