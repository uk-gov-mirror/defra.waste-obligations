using Defra.WasteObligations.Api.Utils.Http;
using Defra.WasteObligations.Api.Utils.OAuth2;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Api.Services.PrnCommonBackend;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPrnCommonBackendService(
        this IServiceCollection services,
        bool addResiliencePipeline = true
    )
    {
        const string name = PrnCommonBackendOptions.SectionName;

        services.AddOAuth2Client<PrnCommonBackendOptions>(name);
        services.AddOptions<HttpStandardResilienceOptions>(name).BindConfiguration(name);

        services
            .AddHttpClient<IPrnCommonBackendService, PrnCommonBackendService>()
            .AddHttpMessageHandler(sp => sp.GetRequiredKeyedService<OAuth2Handler>(name))
            .ConfigurePrimaryHttpMessageHandler<ProxyHttpMessageHandler>()
            .ConfigureHttpClient(
                (sp, httpClient) =>
                {
                    sp.GetRequiredService<IOptions<PrnCommonBackendOptions>>().Value.Configure(httpClient);
                    httpClient.ConfigureForResiliencePipeline(addResiliencePipeline);
                }
            )
            .AddHeaderPropagation()
            .AddResiliencePipeline(addResiliencePipeline, name);

        services
            .AddHttpClient<IOrganisationObligationSource, PrnCommonBackendService>()
            .AddHttpMessageHandler(sp => sp.GetRequiredKeyedService<OAuth2Handler>(name))
            .ConfigurePrimaryHttpMessageHandler<ProxyHttpMessageHandler>()
            .ConfigureHttpClient(
                (sp, httpClient) =>
                {
                    sp.GetRequiredService<IOptions<PrnCommonBackendOptions>>().Value.Configure(httpClient);
                    httpClient.ConfigureForResiliencePipeline(addResiliencePipeline);
                }
            )
            .AddResiliencePipeline(addResiliencePipeline, name);

        return services;
    }
}
