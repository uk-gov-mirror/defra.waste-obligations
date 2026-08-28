using AwesomeAssertions;
using Defra.WasteObligations.Api.Services.OrganisationObligations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Api.Tests.Services.OrganisationObligations;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddOrganisationObligationHydration_ShouldRegisterWorkerByDefault()
    {
        var services = new ServiceCollection();

        services.AddOrganisationObligationHydration();

        services
            .Should()
            .Contain(descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(OrganisationObligationHydrationWorker)
            );
        services
            .Should()
            .Contain(descriptor =>
                descriptor.ServiceType == typeof(IOrganisationObligationRequestPacer)
                && descriptor.ImplementationType == typeof(OrganisationObligationRequestPacer)
                && descriptor.Lifetime == ServiceLifetime.Singleton
            );
    }

    [Fact]
    public void AddOrganisationObligationHydration_WhenWorkerIsDisabled_ShouldValidateConfiguredIntervals()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["OrganisationObligationHydration:LeaseDurationSeconds"] = "60",
                    ["OrganisationObligationHydration:LeaseRenewalIntervalSeconds"] = "30",
                    ["OrganisationObligationHydration:RefreshInterval"] = "00:30:00",
                    ["OrganisationObligationHydration:InitialRetryDelay"] = "00:01:00",
                    ["OrganisationObligationHydration:MaximumRetryDelay"] = "00:30:00",
                    ["OrganisationObligationHydration:MaxDownstreamRequestsPerMinute"] = "20",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddOrganisationObligationHydration(addWorker: false);
        using var serviceProvider = services.BuildServiceProvider();

        var options = serviceProvider.GetRequiredService<IOptions<OrganisationObligationHydrationOptions>>();

        options.Value.PollingEnabled.Should().BeFalse();
        options.Value.LeaseRenewalIntervalSeconds.Should().Be(30);
        options.Value.MaxDownstreamRequestsPerMinute.Should().Be(20);
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IHostedService));
    }
}
