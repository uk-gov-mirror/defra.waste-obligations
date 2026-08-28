using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Api.Services.OrganisationObligations;

public class OrganisationObligationRequestPacer(
    IOptions<OrganisationObligationHydrationOptions> options,
    TimeProvider timeProvider
) : IOrganisationObligationRequestPacer
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

    public async Task Wait(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        DateTimeOffset requestAt;

        try
        {
            var utcNow = timeProvider.GetUtcNow();
            requestAt = _nextRequestAt > utcNow ? _nextRequestAt : utcNow;
            _nextRequestAt = requestAt.Add(PacingInterval());
        }
        finally
        {
            _semaphore.Release();
        }

        var delay = requestAt - timeProvider.GetUtcNow();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, timeProvider, cancellationToken);
        }
    }

    private TimeSpan PacingInterval() => TimeSpan.FromMinutes(1d / options.Value.MaxDownstreamRequestsPerMinute);
}
