using Defra.WasteObligations.Testing.Authentication;
using Defra.WasteObligations.Testing.Extensions.WireMock;

namespace Defra.WasteObligations.Api.IntegrationTests.Scenarios;

public class HealthTests : IntegrationTestBase
{
    [Fact]
    public async Task WhenOrganisationFound_WithObligations_ResponseShouldBeOk()
    {
        const string prnCommonBackendAccessToken = nameof(prnCommonBackendAccessToken);
        const string accountBackendAccessToken = nameof(accountBackendAccessToken);
        await WireMockContext.WireMockAdminApi.StubTokenRequest(
            expiryInSeconds: 60,
            clientId: ClientIds.PrnCommonBackend,
            accessToken: prnCommonBackendAccessToken
        );
        await WireMockContext.WireMockAdminApi.StubTokenRequest(
            expiryInSeconds: 60,
            clientId: ClientIds.AccountBackend,
            accessToken: accountBackendAccessToken
        );
        await WireMockContext.WireMockAdminApi.StubPrnCommonBackendAdminHealth(prnCommonBackendAccessToken);
        await WireMockContext.WireMockAdminApi.StubAccountBackendAdminHealth(accountBackendAccessToken);
        await WireMockContext.WireMockAdminApi.StubWasteOrganisationsHealth(
            BasicAuthCredential.ForClient(ClientIds.WasteOrganisations)
        );
        // see appsettings.json for template IDs
        await WireMockContext.WireMockAdminApi.StubGovukNotifyTemplateRequest(
            GovukNotifyTemplateIds.ComplianceDeclarationSubmissionDirectProducerEnglish
        );
        await WireMockContext.WireMockAdminApi.StubGovukNotifyTemplateRequest(
            GovukNotifyTemplateIds.ComplianceDeclarationSubmissionDirectProducerWelsh
        );
        await WireMockContext.WireMockAdminApi.StubGovukNotifyTemplateRequest(
            GovukNotifyTemplateIds.ComplianceDeclarationSubmissionComplianceSchemeEnglish
        );
        await WireMockContext.WireMockAdminApi.StubGovukNotifyTemplateRequest(
            GovukNotifyTemplateIds.ComplianceDeclarationSubmissionComplianceSchemeWelsh
        );
        await WireMockContext.WireMockAdminApi.StubGovukNotifyTemplateRequest(
            GovukNotifyTemplateIds.ComplianceDeclarationCancellationNotSignedByCorrectPersonEnglish
        );
        await WireMockContext.WireMockAdminApi.StubGovukNotifyTemplateRequest(
            GovukNotifyTemplateIds.ComplianceDeclarationCancellationNotSignedByCorrectPersonWelsh
        );
        await WireMockContext.WireMockAdminApi.StubGovukNotifyTemplateRequest(
            GovukNotifyTemplateIds.ComplianceDeclarationCancellationRecyclingObligationsChangedEnglish
        );
        await WireMockContext.WireMockAdminApi.StubGovukNotifyTemplateRequest(
            GovukNotifyTemplateIds.ComplianceDeclarationCancellationRecyclingObligationsChangedWelsh
        );
        await WireMockContext.WireMockAdminApi.StubGovukNotifyTemplateRequest(
            GovukNotifyTemplateIds.ComplianceDeclarationCancellationCanMeetRecyclingObligationsEnglish
        );
        await WireMockContext.WireMockAdminApi.StubGovukNotifyTemplateRequest(
            GovukNotifyTemplateIds.ComplianceDeclarationCancellationCanMeetRecyclingObligationsWelsh
        );
        await WireMockContext.WireMockAdminApi.StubGovukNotifyTemplateRequest(
            GovukNotifyTemplateIds.ComplianceDeclarationCancellationProducerRequestedEnglish
        );
        await WireMockContext.WireMockAdminApi.StubGovukNotifyTemplateRequest(
            GovukNotifyTemplateIds.ComplianceDeclarationCancellationProducerRequestedWelsh
        );

        var client = CreateClient();

        var response = await client.GetAsync(Testing.Endpoints.Health.All(), TestContext.Current.CancellationToken);

        await VerifyJson(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .DontScrubGuids();
    }
}
