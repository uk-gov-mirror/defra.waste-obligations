using Microsoft.Extensions.DependencyInjection;

namespace Defra.WasteObligations.Api.Services.OrganisationObligations;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOrganisationObligationHydration(
        this IServiceCollection services,
        bool addWorker = true
    )
    {
        services
            .AddOptions<OrganisationObligationHydrationOptions>()
            .BindConfiguration(OrganisationObligationHydrationOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(
                options =>
                    options.LeaseRenewalIntervalSeconds < options.LeaseDurationSeconds
                    && options.RefreshInterval > TimeSpan.Zero
                    && options.InitialRetryDelay > TimeSpan.Zero
                    && options.MaximumRetryDelay >= options.InitialRetryDelay
                    && options.MaximumSummaryStaleness > TimeSpan.Zero
                    && options.OutgoingYearGracePeriod > TimeSpan.Zero,
                "Organisation obligation hydration interval configuration is invalid"
            )
            .ValidateOnStart();
        services.AddTransient<
            IOrganisationObligationHydrationLeaseService,
            OrganisationObligationHydrationLeaseService
        >();
        services.AddSingleton<IOrganisationObligationRequestPacer, OrganisationObligationRequestPacer>();
        services.AddTransient<IOrganisationObligationHydrationService, OrganisationObligationHydrationService>();

        if (addWorker)
        {
            services.AddHostedService<OrganisationObligationHydrationWorker>();
        }

        return services;
    }
}
