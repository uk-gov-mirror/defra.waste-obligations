using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Dtos;
using Defra.WasteObligations.Api.Services;
using Defra.WasteObligations.Api.Services.AccountBackend;
using Defra.WasteObligations.Api.Services.WasteOrganisations;
using Defra.WasteObligations.Testing.Authentication;
using Defra.WasteObligations.Testing.Extensions;
using Defra.WasteObligations.Testing.Extensions.WireMock;
using Defra.WasteObligations.Testing.Fixtures.AccountBackend;
using Defra.WasteObligations.Testing.Fixtures.Dtos;
using Defra.WasteObligations.Testing.Fixtures.WasteOrganisations;
using WasteOrganisationsOrganisationFixture = Defra.WasteObligations.Testing.Fixtures.WasteOrganisations.OrganisationFixture;

namespace Defra.WasteObligations.Api.IntegrationTests.Scenarios;

public class UpdateComplianceDeclarationTests : IntegrationTestBase
{
    private const string Amended = "submission.amended";
    private const string Created = "submission.created";
    private const string Insert = "insert";
    private const string Update = "update";

    [Fact]
    public async Task WhenCreatedAndAccepted_ShouldUpdate()
    {
        var organisationId = Guid.NewGuid();
        using var sqsClient = CreateSqsClient();
        await WireMockContext.WireMockAdminApi.StubWasteOrganisationsOrganisationRequest(
            organisationId,
            BasicAuthCredential.ForClient(ClientIds.WasteOrganisations)
        );
        await WireMockContext.WireMockAdminApi.StubTokenRequest(
            expiryInSeconds: 60,
            clientId: ClientIds.AccountBackend
        );

        var client = CreateClient();

        var response = await client.PostAsJsonAsync(
            Testing.Endpoints.Organisations.ComplianceDeclarations.Create(organisationId),
            CreateComplianceDeclarationRequestFixture.DirectProducer(organisationId).Create(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await response.Content.ReadFromJsonAsync<ComplianceDeclaration>(
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.Should().NotBeNull();
        await AssertAnalyticsEventQueued(sqsClient, result.Id, Insert, Created);

        response = await client.PatchAsJsonAsync(
            Testing.Endpoints.Organisations.ComplianceDeclarations.Update(organisationId, result.Id),
            UpdateComplianceDeclarationRequestFixture.Accepted().Create(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var complianceDeclaration = await client.GetStringAsync(
            Testing.Endpoints.Organisations.ComplianceDeclarations.Read(organisationId, result.Id),
            TestContext.Current.CancellationToken
        );

        await VerifyJson(complianceDeclaration).ScrubTopLevelIdMember().DisableDateCounting();
        await AssertAnalyticsEventQueued(sqsClient, result.Id, Update, Amended);
    }

    [Fact]
    public async Task WhenCreatedAndCancelled_ShouldUpdate()
    {
        var organisationId = Guid.NewGuid();
        using var sqsClient = CreateSqsClient();
        await StubCancellationDependencies(organisationId, directProducer: true, welshOrganisation: false);

        var client = CreateClient();

        var response = await client.PostAsJsonAsync(
            Testing.Endpoints.Organisations.ComplianceDeclarations.Create(organisationId),
            CreateComplianceDeclarationRequestFixture.DirectProducer(organisationId).Create(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await response.Content.ReadFromJsonAsync<ComplianceDeclaration>(
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.Should().NotBeNull();
        await AssertAnalyticsEventQueued(sqsClient, result.Id, Insert, Created);

        response = await client.PatchAsJsonAsync(
            Testing.Endpoints.Organisations.ComplianceDeclarations.Update(organisationId, result.Id),
            UpdateComplianceDeclarationRequestFixture.Cancelled().Create(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var complianceDeclaration = await client.GetStringAsync(
            Testing.Endpoints.Organisations.ComplianceDeclarations.Read(organisationId, result.Id),
            TestContext.Current.CancellationToken
        );

        await VerifyJson(complianceDeclaration).ScrubTopLevelIdMember().DisableDateCounting();
        await AssertAnalyticsEventQueued(sqsClient, result.Id, Update, Amended);

        await AsyncWaiter.WaitForAsync(async () =>
        {
            var entries = await WireMockContext.WireMockAdminApi.GetGovukNotifySendEmail();

            AssertCancelledEmailsSent(
                entries,
                GovukNotifyTemplateIds.ComplianceDeclarationCancellationProducerRequestedEnglish,
                "certificate",
                "tystysgrif"
            );
        });
    }

    [Theory]
    [InlineData(
        ComplianceDeclarationCancellationReasons.NotSignedByCorrectPerson,
        GovukNotifyTemplateIds.ComplianceDeclarationCancellationNotSignedByCorrectPersonEnglish
    )]
    [InlineData(
        ComplianceDeclarationCancellationReasons.RecyclingObligationsChanged,
        GovukNotifyTemplateIds.ComplianceDeclarationCancellationRecyclingObligationsChangedEnglish
    )]
    [InlineData(
        ComplianceDeclarationCancellationReasons.ProducerCanMeetRecyclingObligations,
        GovukNotifyTemplateIds.ComplianceDeclarationCancellationCanMeetRecyclingObligationsEnglish
    )]
    [InlineData(
        ComplianceDeclarationCancellationReasons.ProducerRequestedToCancel,
        GovukNotifyTemplateIds.ComplianceDeclarationCancellationProducerRequestedEnglish
    )]
    public async Task WhenCancelled_ShouldSendEmailForReason(string reason, string expectedTemplateId)
    {
        var organisationId = Guid.NewGuid();
        await StubCancellationDependencies(organisationId, directProducer: true, welshOrganisation: false);

        var client = CreateClient();

        var createResponse = await client.PostAsJsonAsync(
            Testing.Endpoints.Organisations.ComplianceDeclarations.Create(organisationId),
            CreateComplianceDeclarationRequestFixture.DirectProducer(organisationId).Create(),
            TestContext.Current.CancellationToken
        );

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await createResponse.Content.ReadFromJsonAsync<ComplianceDeclaration>(
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.Should().NotBeNull();

        var response = await client.PatchAsJsonAsync(
            Testing.Endpoints.Organisations.ComplianceDeclarations.Update(organisationId, result.Id),
            UpdateComplianceDeclarationRequestFixture.Cancelled(reason).Create(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await AsyncWaiter.WaitForAsync(async () =>
        {
            var entries = await WireMockContext.WireMockAdminApi.GetGovukNotifySendEmail();

            AssertCancelledEmailsSent(entries, expectedTemplateId, "certificate", "tystysgrif");
        });
    }

    [Fact]
    public async Task WhenComplianceSchemeCancelled_ShouldSendStatementCancellationEmails()
    {
        var organisationId = Guid.NewGuid();
        await StubCancellationDependencies(organisationId, directProducer: false, welshOrganisation: false);

        var client = CreateClient();

        var createResponse = await client.PostAsJsonAsync(
            Testing.Endpoints.Organisations.ComplianceDeclarations.Create(organisationId),
            CreateComplianceDeclarationRequestFixture.ComplianceScheme(organisationId).Create(),
            TestContext.Current.CancellationToken
        );

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await createResponse.Content.ReadFromJsonAsync<ComplianceDeclaration>(
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.Should().NotBeNull();

        var response = await client.PatchAsJsonAsync(
            Testing.Endpoints.Organisations.ComplianceDeclarations.Update(organisationId, result.Id),
            UpdateComplianceDeclarationRequestFixture
                .Cancelled(ComplianceDeclarationCancellationReasons.ComplianceSchemeRequestedToCancel)
                .Create(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await AsyncWaiter.WaitForAsync(async () =>
        {
            var entries = await WireMockContext.WireMockAdminApi.GetGovukNotifySendEmail();

            AssertCancelledEmailsSent(
                entries,
                GovukNotifyTemplateIds.ComplianceDeclarationCancellationProducerRequestedEnglish,
                "statement",
                "datganiad"
            );
        });
    }

    [Fact]
    public async Task WhenWelshOrganisationCancelled_ShouldSendWelshCancellationEmail()
    {
        var organisationId = Guid.NewGuid();
        await StubCancellationDependencies(organisationId, directProducer: true, welshOrganisation: true);

        var client = CreateClient();

        var createResponse = await client.PostAsJsonAsync(
            Testing.Endpoints.Organisations.ComplianceDeclarations.Create(organisationId),
            CreateComplianceDeclarationRequestFixture.DirectProducer(organisationId).Create(),
            TestContext.Current.CancellationToken
        );

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await createResponse.Content.ReadFromJsonAsync<ComplianceDeclaration>(
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.Should().NotBeNull();

        var response = await client.PatchAsJsonAsync(
            Testing.Endpoints.Organisations.ComplianceDeclarations.Update(organisationId, result.Id),
            UpdateComplianceDeclarationRequestFixture
                .Cancelled(ComplianceDeclarationCancellationReasons.NotSignedByCorrectPerson)
                .Create(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await AsyncWaiter.WaitForAsync(async () =>
        {
            var entries = await WireMockContext.WireMockAdminApi.GetGovukNotifySendEmail();

            AssertCancelledEmailsSent(
                entries,
                GovukNotifyTemplateIds.ComplianceDeclarationCancellationNotSignedByCorrectPersonWelsh,
                "certificate",
                "tystysgrif"
            );
        });
    }

    private async Task StubCancellationDependencies(Guid organisationId, bool directProducer, bool welshOrganisation)
    {
        if (welshOrganisation)
        {
            await WireMockContext.WireMockAdminApi.StubWasteOrganisationsOrganisationRequest(
                organisationId,
                BasicAuthCredential.ForClient(ClientIds.WasteOrganisations),
                WasteOrganisationsOrganisationFixture
                    .Default(organisationId)
                    .With(x => x.BusinessCountry, BusinessCountry.Wales)
                    .Create()
            );
        }
        else
        {
            await WireMockContext.WireMockAdminApi.StubWasteOrganisationsOrganisationRequest(
                organisationId,
                BasicAuthCredential.ForClient(ClientIds.WasteOrganisations)
            );
        }

        await WireMockContext.WireMockAdminApi.StubTokenRequest(
            expiryInSeconds: 60,
            clientId: ClientIds.AccountBackend
        );
        await WireMockContext.WireMockAdminApi.StubAccountBackendPersonEmailsRequest(
            organisationId,
            directProducer ? EntityTypeCode.DR : EntityTypeCode.CS
        );
    }

    private static void AssertCancelledEmailsSent(
        IList<WireMock.Admin.Requests.LogEntryModel> entries,
        string expectedTemplateId,
        string expectedCertOrStatement,
        string expectedCertOrStatementWelsh,
        string expectedEnvironmentalRegulatorWelsh = "Regulator"
    )
    {
        var cancellationEntries = GetCancelledEmailEntries(entries);
        var recipients = PersonEmailFixture.CancellationRecipients();
        cancellationEntries.Should().HaveCount(recipients.Length);

        foreach (var recipient in recipients)
        {
            cancellationEntries
                .Count(x =>
                    AssertCancelledEmailTemplate(
                        x.Request?.Body,
                        expectedTemplateId,
                        expectedCertOrStatement,
                        expectedCertOrStatementWelsh,
                        expectedEnvironmentalRegulatorWelsh,
                        recipient.FirstName,
                        recipient.LastName,
                        recipient.Email
                    )
                )
                .Should()
                .Be(1);
        }
    }

    private static List<WireMock.Admin.Requests.LogEntryModel> GetCancelledEmailEntries(
        IList<WireMock.Admin.Requests.LogEntryModel> entries
    ) =>
        entries
            .Where(x =>
            {
                if (x.Request?.Body is null)
                    return false;

                using var jsonDocument = JsonDocument.Parse(x.Request.Body);

                return jsonDocument.RootElement.TryGetProperty("personalisation", out var personalisation)
                    && personalisation.TryGetProperty("certOrStatement", out _);
            })
            .ToList();

    private static bool AssertCancelledEmailTemplate(
        string? body,
        string expectedTemplateId,
        string expectedCertOrStatement,
        string expectedCertOrStatementWelsh,
        string expectedEnvironmentalRegulatorWelsh,
        string expectedFirstName,
        string expectedLastName,
        string expectedEmail
    )
    {
        if (body is null)
            return false;

        using var jsonDocument = JsonDocument.Parse(body);
        var personalisation = jsonDocument.RootElement.GetProperty("personalisation");

        if (jsonDocument.RootElement.GetProperty("template_id").GetString() != expectedTemplateId)
            return false;

        if (jsonDocument.RootElement.GetProperty("email_address").GetString() != expectedEmail)
            return false;

        if (personalisation.GetProperty("certOrStatement").GetString() != expectedCertOrStatement)
            return false;

        if (personalisation.GetProperty("certOrStatement_Welsh").GetString() != expectedCertOrStatementWelsh)
            return false;

        if (personalisation.GetProperty("year").GetInt32() != 2026)
            return false;

        if (personalisation.GetProperty("environmentalRegulator").GetString() != "Regulator")
            return false;

        if (
            personalisation.GetProperty("environmentalRegulator_Welsh").GetString()
            != expectedEnvironmentalRegulatorWelsh
        )
            return false;

        if (personalisation.GetProperty("regulatorEmail").GetString() != "regulator@email.com")
            return false;

        if (personalisation.GetProperty("firstName").GetString() != expectedFirstName)
            return false;

        if (personalisation.GetProperty("lastName").GetString() != expectedLastName)
            return false;

        return true;
    }
}
