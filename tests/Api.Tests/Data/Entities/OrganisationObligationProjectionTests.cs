using AwesomeAssertions;
using Defra.WasteObligations.Api.Data.Entities;
using MongoDB.Bson;

namespace Defra.WasteObligations.Api.Tests.Data.Entities;

public class OrganisationObligationProjectionTests
{
    [Fact]
    public void OrganisationObligationSummary_ShouldRetainTheReadModelValues()
    {
        var organisationId = Guid.NewGuid();
        var lastSuccessfulReadAt = new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc);
        var nextRefreshAt = lastSuccessfulReadAt.AddMinutes(30);
        var subject = new OrganisationObligationSummary
        {
            OrganisationId = organisationId,
            ObligationYear = 2026,
            ObligationCount = 2,
            TotalAcceptedTonnage = 40,
            TotalObligatedTonnage = 50,
            RecyclingObligationsMet = false,
            ObligationCoveragePercentage = 80,
            SourceFingerprint = "fingerprint",
            LastSuccessfulReadAt = lastSuccessfulReadAt,
            DailyCalculationRunId = "run-1",
            LastAttemptedAt = lastSuccessfulReadAt,
            NextRefreshAt = nextRefreshAt,
            RefreshState = OrganisationObligationRefreshState.Ready,
            AttemptCount = 1,
        };

        subject.Id.Should().NotBe(ObjectId.Empty);
        subject.OrganisationId.Should().Be(organisationId);
        subject.ObligationYear.Should().Be(2026);
        subject.ObligationCount.Should().Be(2);
        subject.TotalAcceptedTonnage.Should().Be(40);
        subject.TotalObligatedTonnage.Should().Be(50);
        subject.RecyclingObligationsMet.Should().BeFalse();
        subject.ObligationCoveragePercentage.Should().Be(80);
        subject.SourceFingerprint.Should().Be("fingerprint");
        subject.LastSuccessfulReadAt.Should().Be(lastSuccessfulReadAt);
        subject.DailyCalculationRunId.Should().Be("run-1");
        subject.LastAttemptedAt.Should().Be(lastSuccessfulReadAt);
        subject.NextRefreshAt.Should().Be(nextRefreshAt);
        subject.RefreshState.Should().Be(OrganisationObligationRefreshState.Ready);
        subject.AttemptCount.Should().Be(1);
        subject.LastFailure.Should().BeNull();
    }

    [Fact]
    public void OrganisationObligationSummary_ShouldRetainTheHydrationValues()
    {
        var organisationId = Guid.NewGuid();
        var requestedAt = new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc);
        var subject = new OrganisationObligationSummary
        {
            OrganisationId = organisationId,
            ObligationYear = 2026,
            Priority = OrganisationObligationHydrationPriority.NewEligible,
            NextRefreshAt = requestedAt,
            AttemptCount = 1,
            LastFailure = "downstream failure",
            RequestedAt = requestedAt,
            IsHydrationActive = true,
            LastSuccessfulReadAt = null,
        };

        subject.Id.Should().NotBe(ObjectId.Empty);
        subject.OrganisationId.Should().Be(organisationId);
        subject.ObligationYear.Should().Be(2026);
        subject.Priority.Should().Be(OrganisationObligationHydrationPriority.NewEligible);
        subject.NextRefreshAt.Should().Be(requestedAt);
        subject.AttemptCount.Should().Be(1);
        subject.LastFailure.Should().Be("downstream failure");
        subject.RequestedAt.Should().Be(requestedAt);
        subject.IsHydrationActive.Should().BeTrue();
        subject.LastSuccessfulReadAt.Should().BeNull();
    }
}
