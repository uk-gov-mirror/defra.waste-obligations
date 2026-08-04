using System.Diagnostics.CodeAnalysis;
using Defra.WasteObligations.Api.Services.AccountBackend;
using Defra.WasteObligations.Api.Utils.Http;
using Defra.WasteObligations.Api.Utils.OAuth2;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Api.Utils.Health;

[ExcludeFromCodeCoverage]
public class AccountBackendHealthCheck(IServiceProvider serviceProvider) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var proxyHandler = serviceProvider.GetRequiredService<ProxyHttpMessageHandler>();
            var oAuth2Handler = serviceProvider.GetRequiredKeyedService<OAuth2Handler>(
                AccountBackendOptions.SectionName
            );

            oAuth2Handler.InnerHandler = proxyHandler;

            using var httpClient = new HttpClient(oAuth2Handler);

            serviceProvider.GetRequiredService<IOptions<AccountBackendOptions>>().Value.Configure(httpClient);

            const string health = "admin/health";
            var response = await httpClient.GetAsync(health, cancellationToken);

            response.EnsureSuccessStatusCode();

            return HealthCheckResult.Healthy($"Connected to {httpClient.BaseAddress}{health}");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                exception: new Exception($"Failed to connect to {AccountBackendOptions.SectionName}", ex)
            );
        }
    }
}
