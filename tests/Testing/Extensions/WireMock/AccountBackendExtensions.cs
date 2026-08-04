using System.Net;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Services.AccountBackend;
using Defra.WasteObligations.Testing.Fixtures.AccountBackend;
using WireMock.Admin.Mappings;
using WireMock.Client;
using WireMock.Client.Extensions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Defra.WasteObligations.Testing.Extensions.WireMock;

public static class AccountBackendExtensions
{
    public static void StubAccountBackendPersonEmailsRequest(
        this WireMockServer wireMock,
        Guid organisationId,
        EntityTypeCode entityTypeCode,
        string? accessToken = null,
        HttpStatusCode? statusCode = null,
        PersonEmail[]? personEmails = null
    )
    {
        var request = Request
            .Create()
            .UsingGet()
            .WithPath("/api/organisations/person-emails")
            .WithParam("organisationId", organisationId.ToString("D"))
            .WithParam("entityTypeCode", entityTypeCode.ToString());

        if (accessToken is not null)
            request = request.WithHeader("Authorization", $"Bearer {accessToken}");

        wireMock
            .Given(request)
            .RespondWith(
                Response
                    .Create()
                    .WithStatusCode(statusCode ?? HttpStatusCode.OK)
                    .WithBodyAsJson(personEmails ?? PersonEmailFixture.CancellationRecipients())
            );
    }

    public static async Task StubAccountBackendPersonEmailsRequest(
        this IWireMockAdminApi wireMock,
        Guid organisationId,
        EntityTypeCode entityTypeCode,
        string? accessToken = null,
        PersonEmail[]? personEmails = null
    )
    {
        var builder = wireMock.GetMappingBuilder();

        builder.Given(x =>
            x.WithRequest(r =>
                {
                    r.UsingGet()
                        .WithPath("/api/organisations/person-emails")
                        .WithParams(() =>
                            [
                                new ParamModel
                                {
                                    Name = "organisationId",
                                    Matchers =
                                    [
                                        new MatcherModel
                                        {
                                            Name = "ExactMatcher",
                                            Pattern = organisationId.ToString("D"),
                                        },
                                    ],
                                },
                                new ParamModel
                                {
                                    Name = "entityTypeCode",
                                    Matchers =
                                    [
                                        new MatcherModel { Name = "ExactMatcher", Pattern = entityTypeCode.ToString() },
                                    ],
                                },
                            ]
                        );

                    if (accessToken is not null)
                        r.WithHeader("Authorization", $"Bearer {accessToken}");
                })
                .WithResponse(r =>
                    r.WithStatusCode(HttpStatusCode.OK)
                        .WithBodyAsJson(personEmails ?? PersonEmailFixture.CancellationRecipients())
                )
        );

        var status = await builder.BuildAndPostAsync(TestContext.Current.CancellationToken);
        status.Guid.Should().NotBeNull();
    }

    public static async Task StubAccountBackendAdminHealth(this IWireMockAdminApi wireMock, string? accessToken = null)
    {
        var builder = wireMock.GetMappingBuilder();

        builder.Given(x =>
            x.WithRequest(r =>
                {
                    r.UsingGet().WithPath("/admin/health");

                    if (accessToken is not null)
                        r.WithHeader("Authorization", $"Bearer {accessToken}");
                })
                .WithResponse(r => r.WithStatusCode(HttpStatusCode.OK))
        );

        var status = await builder.BuildAndPostAsync(TestContext.Current.CancellationToken);
        status.Guid.Should().NotBeNull();
    }
}
