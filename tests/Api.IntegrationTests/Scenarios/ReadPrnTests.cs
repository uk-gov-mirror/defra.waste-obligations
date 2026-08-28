using System.Net;
using System.Net.Http.Json;
using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Dtos;
using Defra.WasteObligations.Api.Services.PrnCommonBackend;
using Defra.WasteObligations.Testing;
using Defra.WasteObligations.Testing.Authentication;
using Defra.WasteObligations.Testing.Extensions.WireMock;
using Defra.WasteObligations.Testing.Fixtures.PrnCommonBackend;

namespace Defra.WasteObligations.Api.IntegrationTests.Scenarios;

public class ReadPrnTests : IntegrationTestBase
{
    [Fact]
    public async Task WhenOrganisationAndPrnFound_ResponseShouldContainMappedPrn()
    {
        await WireMockContext.WireMockAdminApi.StubTokenRequest(
            expiryInSeconds: 60,
            clientId: ClientIds.PrnCommonBackend
        );
        var organisationId = Guid.NewGuid();
        await WireMockContext.WireMockAdminApi.StubWasteOrganisationsOrganisationRequest(
            organisationId,
            BasicAuthCredential.ForClient(ClientIds.WasteOrganisations)
        );
        var prn = PrnDataFixture.Default().With(x => x.OrganisationId, organisationId).Create();
        await WireMockContext.WireMockAdminApi.StubPrnCommonBackendPrnRequest(
            prn.ExternalId,
            prn,
            organisationId.ToString("D"),
            OAuth2Extensions.AccessToken
        );

        var client = CreateClient();

        var response = await client.GetAsync(
            Testing.Endpoints.Organisations.Prns.Read(organisationId, prn.ExternalId.ToString("D")),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = await response.Content.ReadFromJsonAsync<Prn>(TestContext.Current.CancellationToken);
        result.Should().BeEquivalentTo(prn.ToDto());
        await VerifyJson(responseBody).DontScrubDateTimes();
    }
}
