using System.Diagnostics.CodeAnalysis;
using Defra.WasteObligations.AuditEvents.Metrics;

namespace Defra.WasteObligations.Api.Utils.Metrics;

[ExcludeFromCodeCoverage]
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRequestMetrics(this IServiceCollection services)
    {
        services.AddMetrics();
        services.AddTransient<MetricsMiddleware>();
        services.AddSingleton<IRequestMetrics, RequestMetrics>();
        services.AddSingleton<IComplianceDeclarationMetrics, ComplianceDeclarationMetrics>();
        services.AddSingleton<IEmailMetrics, EmailMetrics>();
        services.AddSingleton<IOrganisationObligationHydrationMetrics, OrganisationObligationHydrationMetrics>();
        services.AddSingleton<IAuditEventMetrics, AuditEventMetrics>();

        return services;
    }
}
