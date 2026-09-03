using System.Net;
using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Services.PrnCommonBackend;
using Defra.WasteObligations.Api.Utils.Http;
using Defra.WasteObligations.Testing;
using Defra.WasteObligations.Testing.Extensions.WireMock;
using Defra.WasteObligations.Testing.Fixtures.PrnCommonBackend;
using Microsoft.AspNetCore.HeaderPropagation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Defra.WasteObligations.Api.Tests.Services.PrnCommonBackend;

public class PrnCommonBackendServiceTests : WireMockTestBase
{
    private const string TraceHeaderName = "x-cdp-request-id";
    private const string TraceId = "trace-id";

    private ServiceCollection Services { get; }

    public PrnCommonBackendServiceTests(WireMockContext context)
        : base(context)
    {
        var config = new Dictionary<string, string?>
        {
            { $"{PrnCommonBackendOptions.SectionName}:BaseAddress", context.BaseAddress },
            { $"{PrnCommonBackendOptions.SectionName}:TokenEndpoint", $"{context.BaseAddress}/token" },
            { $"{PrnCommonBackendOptions.SectionName}:ClientId", "client_id" },
            { $"{PrnCommonBackendOptions.SectionName}:ClientSecret", "client_secret" },
            { $"{PrnCommonBackendOptions.SectionName}:Scope", "scope" },
            { $"{PrnCommonBackendOptions.SectionName}:TotalRequestTimeout:Timeout", "00:00:40" },
            { $"{PrnCommonBackendOptions.SectionName}:AttemptTimeout:Timeout", "00:00:05" },
        };

        Services = [];
        Services.AddHttpContextAccessor();
        Services.AddHeaderPropagation(options => options.Headers.Add(TraceHeaderName));
        Services.AddPrnCommonBackendService();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        Services.TryAddSingleton<HeaderPropagationValues>();
        Services.AddTransient<ProxyHttpMessageHandler>();
    }

    [Fact]
    public async Task RequiredService_ShouldNotBeNull()
    {
        await using var sp = Services.BuildServiceProvider();

        var service = sp.GetService<IPrnCommonBackendService>();

        service.Should().NotBeNull();
    }

    [Fact]
    public async Task OrganisationObligationSource_ShouldNotBeNull()
    {
        await using var sp = Services.BuildServiceProvider();

        var service = sp.GetService<IOrganisationObligationSource>();

        service.Should().NotBeNull();
    }

    [Fact]
    public async Task ReadObligations_ShouldReturnData()
    {
        await using var sp = Services.BuildServiceProvider();

        var service = sp.GetRequiredService<IPrnCommonBackendService>();
        sp.GetRequiredService<HeaderPropagationValues>().Headers = new Dictionary<string, StringValues>();
        const int year = 2026;
        const string accessToken = "access_token";

        WireMock.StubTokenRequest();
        WireMock.StubPrnCommonBackendObligationsRequest(
            year,
            ObligationFixture.OrganisationId.ToString("D"),
            accessToken
        );

        var obligations = (
            await service.ReadObligations(ObligationFixture.OrganisationId, year, TestContext.Current.CancellationToken)
        ).ToList();

        obligations.Should().ContainSingle();
    }

    [Fact]
    public async Task ReadObligations_ShouldPropagateTraceHeader()
    {
        await using var sp = Services.BuildServiceProvider();

        var service = sp.GetRequiredService<IPrnCommonBackendService>();
        sp.GetRequiredService<HeaderPropagationValues>().Headers = new Dictionary<string, StringValues>
        {
            [TraceHeaderName] = TraceId,
        };
        const int year = 2026;

        WireMock.StubTokenRequest();
        WireMock.StubPrnCommonBackendObligationsRequest(year, ObligationFixture.OrganisationId.ToString("D"));

        await service.ReadObligations(ObligationFixture.OrganisationId, year, TestContext.Current.CancellationToken);

        var request = WireMock
            .LogEntries.Single(x => x.RequestMessage?.Path == $"/api/v1/prn/obligationcalculation/{year}")
            .RequestMessage;
        request.Should().NotBeNull();
        request.Headers.Should().ContainKey(TraceHeaderName).WhoseValue.Should().Contain(TraceId);
    }

    [Fact]
    public async Task OrganisationObligationSource_ShouldNotPropagateTraceHeader()
    {
        await using var sp = Services.BuildServiceProvider();

        var service = sp.GetRequiredService<IOrganisationObligationSource>();
        sp.GetRequiredService<HeaderPropagationValues>().Headers = new Dictionary<string, StringValues>
        {
            [TraceHeaderName] = TraceId,
        };
        const int year = 2026;

        WireMock.StubTokenRequest();
        WireMock.StubPrnCommonBackendObligationsRequest(year, ObligationFixture.OrganisationId.ToString("D"));

        await service.ReadObligations(ObligationFixture.OrganisationId, year, TestContext.Current.CancellationToken);

        var request = WireMock
            .LogEntries.Single(x => x.RequestMessage?.Path == $"/api/v1/prn/obligationcalculation/{year}")
            .RequestMessage;
        request.Should().NotBeNull();
        request.Headers.Should().NotContainKey(TraceHeaderName);
    }

    [Fact]
    public async Task WhenNotFound_ShouldBeEmpty()
    {
        var subject = new PrnCommonBackendService(Context.HttpClient);

        var result = await subject.ReadObligations(Guid.NewGuid(), 2026, TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadPrn_ShouldReturnPrnData()
    {
        await using var sp = Services.BuildServiceProvider();

        var service = sp.GetRequiredService<IPrnCommonBackendService>();
        sp.GetRequiredService<HeaderPropagationValues>().Headers = new Dictionary<string, StringValues>();
        const string accessToken = "access_token";
        var prn = PrnDataFixture.Default().Create();

        WireMock.StubTokenRequest();
        WireMock.StubPrnCommonBackendPrnRequest(prn.ExternalId, prn, prn.OrganisationId.ToString("D"), accessToken);

        var result = await service.ReadPrn(
            prn.OrganisationId,
            prn.ExternalId.ToString("D"),
            TestContext.Current.CancellationToken
        );

        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(prn);
    }

    [Fact]
    public async Task ReadPrn_WhenNotFound_ShouldReturnNull()
    {
        var subject = new PrnCommonBackendService(Context.HttpClient);

        var result = await subject.ReadPrn(
            Guid.NewGuid(),
            Guid.NewGuid().ToString("D"),
            TestContext.Current.CancellationToken
        );

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReadPrn_WhenPrnIdNotGuid_ShouldReturnNull()
    {
        var subject = new PrnCommonBackendService(Context.HttpClient);

        var result = await subject.ReadPrn(Guid.NewGuid(), "not-a-guid", TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Fact]
    public async Task UpdatePrnStatus_ShouldSendStatusUpdate()
    {
        await using var sp = Services.BuildServiceProvider();

        var service = sp.GetRequiredService<IPrnCommonBackendService>();
        sp.GetRequiredService<HeaderPropagationValues>().Headers = new Dictionary<string, StringValues>();
        const string accessToken = "access_token";
        var organisationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var statusUpdate = new PrnStatusUpdate { PrnId = Guid.NewGuid(), Status = "ACCEPTED" };

        WireMock.StubTokenRequest();
        WireMock.StubPrnCommonBackendPrnStatusUpdateRequest(
            statusUpdate,
            organisationId,
            userId,
            accessToken: accessToken
        );

        var result = await service.UpdatePrnStatus(
            organisationId,
            userId,
            statusUpdate.PrnId.ToString("D"),
            statusUpdate.Status,
            TestContext.Current.CancellationToken
        );

        result.Should().Be(PrnStatusUpdateResult.Updated);
    }

    [Fact]
    public async Task UpdatePrnStatus_WhenPrnCommonBackendReturnsNotFound_ShouldReturnNotFound()
    {
        var subject = new PrnCommonBackendService(Context.HttpClient);
        var organisationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var statusUpdate = new PrnStatusUpdate { PrnId = Guid.NewGuid(), Status = "REJECTED" };

        WireMock.StubPrnCommonBackendPrnStatusUpdateRequest(
            statusUpdate,
            organisationId,
            userId,
            HttpStatusCode.NotFound
        );

        var result = await subject.UpdatePrnStatus(
            organisationId,
            userId,
            statusUpdate.PrnId.ToString("D"),
            statusUpdate.Status,
            TestContext.Current.CancellationToken
        );

        result.Should().Be(PrnStatusUpdateResult.NotFound);
    }

    [Fact]
    public async Task UpdatePrnStatus_WhenPrnCommonBackendReturnsConflict_ShouldThrowConcurrencyException()
    {
        var subject = new PrnCommonBackendService(Context.HttpClient);
        var organisationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var statusUpdate = new PrnStatusUpdate { PrnId = Guid.NewGuid(), Status = "REJECTED" };

        WireMock.StubPrnCommonBackendPrnStatusUpdateRequest(
            statusUpdate,
            organisationId,
            userId,
            HttpStatusCode.Conflict
        );

        var act = () =>
            subject.UpdatePrnStatus(
                organisationId,
                userId,
                statusUpdate.PrnId.ToString("D"),
                statusUpdate.Status,
                TestContext.Current.CancellationToken
            );

        await act.Should().ThrowAsync<ConcurrencyException>();
    }

    [Fact]
    public async Task UpdatePrnStatus_WhenPrnCommonBackendReturnsUnexpectedError_ShouldThrow()
    {
        var subject = new PrnCommonBackendService(Context.HttpClient);
        var organisationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var statusUpdate = new PrnStatusUpdate { PrnId = Guid.NewGuid(), Status = "REJECTED" };

        WireMock.StubPrnCommonBackendPrnStatusUpdateRequest(
            statusUpdate,
            organisationId,
            userId,
            HttpStatusCode.InternalServerError
        );

        var act = () =>
            subject.UpdatePrnStatus(
                organisationId,
                userId,
                statusUpdate.PrnId.ToString("D"),
                statusUpdate.Status,
                TestContext.Current.CancellationToken
            );

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task UpdatePrnStatus_WhenPrnCommonBackendReturnsOtherSuccessfulResponse_ShouldReturnUpdated()
    {
        var subject = new PrnCommonBackendService(Context.HttpClient);
        var organisationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var statusUpdate = new PrnStatusUpdate { PrnId = Guid.NewGuid(), Status = "REJECTED" };

        WireMock.StubPrnCommonBackendPrnStatusUpdateRequest(
            statusUpdate,
            organisationId,
            userId,
            HttpStatusCode.NoContent
        );

        var result = await subject.UpdatePrnStatus(
            organisationId,
            userId,
            statusUpdate.PrnId.ToString("D"),
            statusUpdate.Status,
            TestContext.Current.CancellationToken
        );

        result.Should().Be(PrnStatusUpdateResult.Updated);
    }

    [Fact]
    public async Task UpdatePrnStatus_WhenPrnIdIsNotGuid_ShouldReturnNotFound()
    {
        var subject = new PrnCommonBackendService(Context.HttpClient);

        var result = await subject.UpdatePrnStatus(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "not-a-guid",
            "ACCEPTED",
            TestContext.Current.CancellationToken
        );

        result.Should().Be(PrnStatusUpdateResult.NotFound);
    }

    [Fact]
    public async Task SearchPrns_ShouldReturnSearchResponse()
    {
        await using var sp = Services.BuildServiceProvider();

        var service = sp.GetRequiredService<IPrnCommonBackendService>();
        sp.GetRequiredService<HeaderPropagationValues>().Headers = new Dictionary<string, StringValues>();
        const string accessToken = "access_token";
        var search = new PrnSearchRequest
        {
            Page = 2,
            PageSize = 50,
            Search = "PRN123",
            FilterBy = "accepted-all",
            SortBy = "tonnage-desc",
        };
        var response = new PrnSearchResponse { Items = [PrnDataFixture.Default().Create()], TotalItems = 51 };

        WireMock.StubTokenRequest();
        WireMock.StubPrnCommonBackendPrnSearchRequest(
            search,
            response,
            PrnDataFixture.OrganisationId.ToString("D"),
            accessToken
        );

        var result = await service.SearchPrns(
            PrnDataFixture.OrganisationId,
            search,
            TestContext.Current.CancellationToken
        );

        result.Should().BeEquivalentTo(response);
    }

    [Fact]
    public async Task SearchPrns_WhenSortIsNotSpecified_ShouldNotSendSortBy()
    {
        var subject = new PrnCommonBackendService(Context.HttpClient);
        var search = new PrnSearchRequest { Page = 1, PageSize = 20 };

        WireMock.StubPrnCommonBackendPrnSearchRequest(search);

        await subject.SearchPrns(Guid.NewGuid(), search, TestContext.Current.CancellationToken);

        var request = WireMock.LogEntries.Single(x => x.RequestMessage?.Path == "/api/v1/prn/search").RequestMessage;
        request.Should().NotBeNull();
        request.RawQuery.Should().NotContain("sortBy");
    }

    [Fact]
    public async Task SearchPrns_WhenPrnCommonBackendReturnsNull_ShouldThrow()
    {
        var subject = new PrnCommonBackendService(Context.HttpClient);
        var search = new PrnSearchRequest
        {
            Page = 1,
            PageSize = 20,
            SortBy = "date-issued-desc",
        };

        WireMock
            .Given(Request.Create().UsingGet().WithPath("/api/v1/prn/search"))
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.OK).WithBody("null"));

        var act = () => subject.SearchPrns(Guid.NewGuid(), search, TestContext.Current.CancellationToken);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("PRN common backend returned an empty search response");
    }
}
