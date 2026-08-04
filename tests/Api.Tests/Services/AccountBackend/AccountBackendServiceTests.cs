using System.Net;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Services.AccountBackend;
using Defra.WasteObligations.Api.Utils.Http;
using Defra.WasteObligations.Testing;
using Defra.WasteObligations.Testing.Extensions.WireMock;
using Defra.WasteObligations.Testing.Fixtures.AccountBackend;
using Microsoft.AspNetCore.HeaderPropagation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;

namespace Defra.WasteObligations.Api.Tests.Services.AccountBackend;

public class AccountBackendServiceTests : WireMockTestBase
{
    private ServiceCollection Services { get; }

    public AccountBackendServiceTests(WireMockContext context)
        : base(context)
    {
        var config = new Dictionary<string, string?>
        {
            { $"{AccountBackendOptions.SectionName}:BaseAddress", context.BaseAddress },
            { $"{AccountBackendOptions.SectionName}:TokenEndpoint", $"{context.BaseAddress}/token" },
            { $"{AccountBackendOptions.SectionName}:ClientId", "client_id" },
            { $"{AccountBackendOptions.SectionName}:ClientSecret", "client_secret" },
            { $"{AccountBackendOptions.SectionName}:Scope", "scope" },
            { $"{AccountBackendOptions.SectionName}:TotalRequestTimeout:Timeout", "00:00:40" },
            { $"{AccountBackendOptions.SectionName}:AttemptTimeout:Timeout", "00:00:05" },
        };

        Services = [];
        Services.AddAccountBackendService();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        Services.TryAddSingleton<HeaderPropagationValues>();
        Services.AddTransient<ProxyHttpMessageHandler>();
    }

    [Fact]
    public async Task RequiredService_ShouldNotBeNull()
    {
        await using var sp = Services.BuildServiceProvider();

        var service = sp.GetService<IAccountBackendService>();

        service.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadPersonEmails_ShouldReturnData()
    {
        await using var sp = Services.BuildServiceProvider();

        var service = sp.GetRequiredService<IAccountBackendService>();
        sp.GetRequiredService<HeaderPropagationValues>().Headers = new Dictionary<string, StringValues>();

        var organisationId = Guid.NewGuid();
        const EntityTypeCode entityTypeCode = EntityTypeCode.CS;
        const string accessToken = "access_token";

        WireMock.StubTokenRequest();
        WireMock.StubAccountBackendPersonEmailsRequest(
            organisationId,
            entityTypeCode,
            accessToken,
            personEmails: [PersonEmailFixture.Default()]
        );

        var emails = (
            await service.ReadPersonEmails(organisationId, entityTypeCode, TestContext.Current.CancellationToken)
        ).ToList();

        emails.Should().BeEquivalentTo([PersonEmailFixture.Default()]);
    }

    [Fact]
    public async Task ReadPersonEmails_WhenNoContent_ShouldBeEmpty()
    {
        await using var sp = Services.BuildServiceProvider();

        var service = sp.GetRequiredService<IAccountBackendService>();
        sp.GetRequiredService<HeaderPropagationValues>().Headers = new Dictionary<string, StringValues>();

        var organisationId = Guid.NewGuid();
        const EntityTypeCode entityTypeCode = EntityTypeCode.CS;
        const string accessToken = "access_token";

        WireMock.StubTokenRequest();
        WireMock.StubAccountBackendPersonEmailsRequest(
            organisationId,
            entityTypeCode,
            accessToken,
            HttpStatusCode.NoContent
        );

        var emails = (
            await service.ReadPersonEmails(organisationId, entityTypeCode, TestContext.Current.CancellationToken)
        ).ToList();

        emails.Should().BeEmpty();
    }
}
