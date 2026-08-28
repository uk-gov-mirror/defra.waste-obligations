using AwesomeAssertions;
using Defra.WasteObligations.Api.Services.OrganisationObligations;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Defra.WasteObligations.Api.Tests.Services.OrganisationObligations;

public class OrganisationObligationRequestPacerTests
{
    [Fact]
    public async Task Wait_ShouldEvenlySpaceRequestsAtTheConfiguredRate()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        var subject = new OrganisationObligationRequestPacer(
            Options.Create(new OrganisationObligationHydrationOptions { MaxDownstreamRequestsPerMinute = 20 }),
            timeProvider
        );

        await subject.Wait(TestContext.Current.CancellationToken);
        var secondRequest = subject.Wait(TestContext.Current.CancellationToken);

        secondRequest.IsCompleted.Should().BeFalse();
        timeProvider.Advance(TimeSpan.FromSeconds(3));
        await secondRequest;
    }
}
