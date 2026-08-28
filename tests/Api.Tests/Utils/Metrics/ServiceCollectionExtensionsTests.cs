using AwesomeAssertions;
using Defra.WasteObligations.Api.Utils.Metrics;
using Defra.WasteObligations.AuditEvents.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace Defra.WasteObligations.Api.Tests.Utils.Metrics;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddRequestMetrics_ShouldRegisterAuditEventMetrics()
    {
        var services = new ServiceCollection();

        services.AddRequestMetrics();

        services
            .Should()
            .Contain(x =>
                x.ServiceType == typeof(IAuditEventMetrics) && x.ImplementationType == typeof(AuditEventMetrics)
            );
    }

    [Fact]
    public void AddRequestMetrics_ShouldRegisterEmailMetrics()
    {
        var services = new ServiceCollection();

        services.AddRequestMetrics();

        services
            .Should()
            .Contain(x => x.ServiceType == typeof(IEmailMetrics) && x.ImplementationType == typeof(EmailMetrics));
    }

    [Fact]
    public void AddRequestMetrics_ShouldRegisterOrganisationObligationHydrationMetrics()
    {
        var services = new ServiceCollection();

        services.AddRequestMetrics();

        services
            .Should()
            .Contain(x =>
                x.ServiceType == typeof(IOrganisationObligationHydrationMetrics)
                && x.ImplementationType == typeof(OrganisationObligationHydrationMetrics)
            );
    }
}
