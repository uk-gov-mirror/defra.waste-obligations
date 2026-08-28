using System.ComponentModel.DataAnnotations;

namespace Defra.WasteObligations.Api.Services.OrganisationObligations;

public record OrganisationObligationHydrationOptions
{
    public const string SectionName = "OrganisationObligationHydration";

    public bool PollingEnabled { get; init; }

    [Range(1, 86400)]
    public int PollIntervalSeconds { get; init; } = 60;

    [Range(2, 3600)]
    public int LeaseDurationSeconds { get; init; } = 300;

    [Range(1, 300)]
    public int LeaseRenewalIntervalSeconds { get; init; } = 60;

    [Range(1, 100)]
    public int BatchSize { get; init; } = 10;

    [Range(1, 20)]
    public int MaxConcurrentRequests { get; init; } = 2;

    [Range(1, 120)]
    public int MaxDownstreamRequestsPerMinute { get; init; } = 20;

    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan MaximumSummaryStaleness { get; init; } = TimeSpan.FromHours(2);

    public TimeSpan OutgoingYearGracePeriod { get; init; } = TimeSpan.FromHours(1);
}
