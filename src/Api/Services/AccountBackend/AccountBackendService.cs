using System.Net;
using Defra.WasteObligations.Api.Utils.Http;

namespace Defra.WasteObligations.Api.Services.AccountBackend;

public class AccountBackendService(HttpClient httpClient) : IAccountBackendService
{
    public async Task<IEnumerable<PersonEmail>> ReadPersonEmails(
        Guid organisationId,
        EntityTypeCode entityTypeCode,
        CancellationToken cancellationToken
    )
    {
        var request = httpClient.CreateRequest(
            HttpMethod.Get,
            $"api/organisations/person-emails?organisationId={organisationId:D}&entityTypeCode={entityTypeCode}"
        );

        var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<IEnumerable<PersonEmail>>(cancellationToken) ?? [];
    }
}
