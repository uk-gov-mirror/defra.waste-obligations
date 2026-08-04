using AwesomeAssertions;
using Defra.WasteObligations.Api.Services.GovukNotify;
using Defra.WasteObligations.Api.Utils.Http;
using Defra.WasteObligations.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Notify.Client;
using Notify.Interfaces;
using NSubstitute;

namespace Defra.WasteObligations.Api.Tests.Services.GovukNotify;

public class GovukNotifyServiceTests : WireMockTestBase
{
    private const string DirectProducerEnglishTemplateId = "direct_producer_en_template_id";
    private const string DirectProducerWelshTemplateId = "direct_producer_cy_template_id";
    private const string ComplianceSchemeEnglishTemplateId = "compliance_scheme_en_template_id";
    private const string ComplianceSchemeWelshTemplateId = "compliance_scheme_cy_template_id";

    private ServiceCollection Services { get; }
    private IAsyncNotificationClient NotificationClient { get; } = Substitute.For<IAsyncNotificationClient>();

    public GovukNotifyServiceTests(WireMockContext context)
        : base(context)
    {
        var config = new Dictionary<string, string?>
        {
            { $"{GovukNotifyOptions.SectionName}:ApiKey", "dummyapikey" },
            {
                $"{GovukNotifyOptions.SectionName}:Templates:{nameof(GovukNotifyOptions.TemplateName.ComplianceDeclarationSubmissionDirectProducer)}:TemplateId:En",
                DirectProducerEnglishTemplateId
            },
            {
                $"{GovukNotifyOptions.SectionName}:Templates:{nameof(GovukNotifyOptions.TemplateName.ComplianceDeclarationSubmissionDirectProducer)}:TemplateId:Cy",
                DirectProducerWelshTemplateId
            },
            {
                $"{GovukNotifyOptions.SectionName}:Templates:{nameof(GovukNotifyOptions.TemplateName.ComplianceDeclarationSubmissionComplianceScheme)}:TemplateId:En",
                ComplianceSchemeEnglishTemplateId
            },
            {
                $"{GovukNotifyOptions.SectionName}:Templates:{nameof(GovukNotifyOptions.TemplateName.ComplianceDeclarationSubmissionComplianceScheme)}:TemplateId:Cy",
                ComplianceSchemeWelshTemplateId
            },
            { $"{GovukNotifyOptions.SectionName}:TotalRequestTimeout:Timeout", "00:00:40" },
            { $"{GovukNotifyOptions.SectionName}:AttemptTimeout:Timeout", "00:00:05" },
        };

        Services = [];
        Services.AddGovukNotify();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        Services.AddTransient<ProxyHttpMessageHandler>();
        Services.AddGovukNotifyFactory(_ =>
            (_, options) =>
            {
                options.ApiKey.Should().Be("dummyapikey");

                return NotificationClient;
            }
        );
    }

    [Fact]
    public async Task RequiredService_ShouldNotBeNull()
    {
        await using var sp = Services.BuildServiceProvider();

        var service = sp.GetService<IGovukNotifyService>();

        service.Should().NotBeNull();
    }

    [Theory]
    [InlineData(
        GovukNotifyOptions.TemplateName.ComplianceDeclarationSubmissionDirectProducer,
        "en",
        DirectProducerEnglishTemplateId
    )]
    [InlineData(
        GovukNotifyOptions.TemplateName.ComplianceDeclarationSubmissionDirectProducer,
        "cy",
        DirectProducerWelshTemplateId
    )]
    [InlineData(
        GovukNotifyOptions.TemplateName.ComplianceDeclarationSubmissionComplianceScheme,
        "en",
        ComplianceSchemeEnglishTemplateId
    )]
    [InlineData(
        GovukNotifyOptions.TemplateName.ComplianceDeclarationSubmissionComplianceScheme,
        "cy",
        ComplianceSchemeWelshTemplateId
    )]
    public async Task SendComplianceDeclarationSubmittedEmail_ShouldSend(
        GovukNotifyOptions.TemplateName template,
        string language,
        string expectedTemplateId
    )
    {
        await using var sp = Services.BuildServiceProvider();

        var service = sp.GetRequiredService<IGovukNotifyService>();

        await service.SendComplianceDeclarationSubmittedEmail(
            template,
            ["email1@email.com", "email2@email.com"],
            new Dictionary<string, object> { { "key1", "value1" } },
            language
        );

        await NotificationClient
            .Received()
            .SendEmailAsync(
                "email1@email.com",
                expectedTemplateId,
                Arg.Is<Dictionary<string, object>>(x => x.Count == 1 && (string)x["key1"] == "value1")
            );
        await NotificationClient
            .Received()
            .SendEmailAsync(
                "email2@email.com",
                expectedTemplateId,
                Arg.Is<Dictionary<string, object>>(x => x.Count == 1 && (string)x["key1"] == "value1")
            );
    }

    [Fact]
    public async Task SendComplianceDeclarationCancelledEmail_ShouldSendPerRecipientPersonalisation()
    {
        const string cancellationEnglishTemplateId = "cancellation_en_template_id";

        var config = new Dictionary<string, string?>
        {
            { $"{GovukNotifyOptions.SectionName}:ApiKey", "dummyapikey" },
            {
                $"{GovukNotifyOptions.SectionName}:Templates:{nameof(GovukNotifyOptions.TemplateName.ComplianceDeclarationCancellationProducerRequested)}:TemplateId:En",
                cancellationEnglishTemplateId
            },
        };

        ServiceCollection services = [];
        services.AddGovukNotify();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.AddTransient<ProxyHttpMessageHandler>();
        services.AddGovukNotifyFactory(_ => (_, _) => NotificationClient);

        await using var sp = services.BuildServiceProvider();

        var service = sp.GetRequiredService<IGovukNotifyService>();

        await service.SendComplianceDeclarationCancelledEmail(
            GovukNotifyOptions.TemplateName.ComplianceDeclarationCancellationProducerRequested,
            [
                ("email1@email.com", new Dictionary<string, object> { { "firstName", "First1" } }),
                ("email2@email.com", new Dictionary<string, object> { { "firstName", "First2" } }),
            ],
            "en"
        );

        await NotificationClient
            .Received()
            .SendEmailAsync(
                "email1@email.com",
                cancellationEnglishTemplateId,
                Arg.Is<Dictionary<string, object>>(x => (string)x["firstName"] == "First1")
            );
        await NotificationClient
            .Received()
            .SendEmailAsync(
                "email2@email.com",
                cancellationEnglishTemplateId,
                Arg.Is<Dictionary<string, object>>(x => (string)x["firstName"] == "First2")
            );
    }

    [Fact]
    public async Task InvalidTemplateName_Throws()
    {
        var config = new Dictionary<string, string?>
        {
            { $"{GovukNotifyOptions.SectionName}:ApiKey", "dummyapikey" },
            { $"{GovukNotifyOptions.SectionName}:Templates:InvalidTemplateName:TemplateId:En", "en_template_id" },
        };

        ServiceCollection services = [];
        services.AddGovukNotify();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.AddTransient<ProxyHttpMessageHandler>();
        services.AddGovukNotifyFactory(_ => (_, _) => NotificationClient);

        await using var sp = services.BuildServiceProvider();

        // ReSharper disable once AccessToDisposedClosure
        var act = () => sp.GetRequiredService<IOptions<GovukNotifyOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public async Task NotificationClientFactory_InDevelopment_CanOverrideBaseAddress()
    {
        var webHostEnvironment = Substitute.For<IWebHostEnvironment>();
        webHostEnvironment.EnvironmentName.Returns("Development");

        ServiceCollection services = [];
        services.AddGovukNotify();
        services.AddSingleton<IWebHostEnvironment>(_ => webHostEnvironment);

        await using var sp = services.BuildServiceProvider();

        var factory = sp.GetGovukNotifyFactory();

        var notificationClient =
            factory(
                new HttpClient(),
                new GovukNotifyOptions
                {
                    ApiKey = "dummyapikey-00000000-0000-0000-0000-000000000000-00000000-0000-0000-0000-000000000000",
                    BaseAddress = "http://baseaddress",
                }
            ) as NotificationClient;

        notificationClient.Should().NotBeNull();
        notificationClient.BaseUrl.Should().Be("http://baseaddress");
    }

    [Fact]
    public async Task NotificationClientFactory_InProduction_CannotOverrideBaseAddress()
    {
        var webHostEnvironment = Substitute.For<IWebHostEnvironment>();
        webHostEnvironment.EnvironmentName.Returns("Production");

        ServiceCollection services = [];
        services.AddGovukNotify();
        services.AddSingleton<IWebHostEnvironment>(_ => webHostEnvironment);

        await using var sp = services.BuildServiceProvider();

        var factory = sp.GetGovukNotifyFactory();

        var notificationClient =
            factory(
                new HttpClient(),
                new GovukNotifyOptions
                {
                    ApiKey = "dummyapikey-00000000-0000-0000-0000-000000000000-00000000-0000-0000-0000-000000000000",
                    BaseAddress = "http://baseaddress",
                }
            ) as NotificationClient;

        notificationClient.Should().NotBeNull();
        notificationClient.BaseUrl.Should().Be("https://api.notifications.service.gov.uk/");
    }
}
