using Defra.WasteObligations.Api.Utils.Http;
using Defra.WasteObligations.Api.Utils.OAuth2;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Api.Services.AccountBackend;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAccountBackendService(
        this IServiceCollection services,
        bool addResiliencePipeline = true
    )
    {
        const string name = AccountBackendOptions.SectionName;

        services.AddOAuth2Client<AccountBackendOptions>(name);
        services.AddOptions<HttpStandardResilienceOptions>(name).BindConfiguration(name);

        services
            .AddHttpClient<IAccountBackendService, AccountBackendService>()
            .AddHttpMessageHandler(sp => sp.GetRequiredKeyedService<OAuth2Handler>(name))
            .ConfigurePrimaryHttpMessageHandler<ProxyHttpMessageHandler>()
            .ConfigureHttpClient(
                (sp, httpClient) =>
                {
                    sp.GetRequiredService<IOptions<AccountBackendOptions>>().Value.Configure(httpClient);
                    httpClient.ConfigureForResiliencePipeline(addResiliencePipeline);
                }
            )
            .AddHeaderPropagation()
            .AddResiliencePipeline(addResiliencePipeline, name);

        return services;
    }
}
