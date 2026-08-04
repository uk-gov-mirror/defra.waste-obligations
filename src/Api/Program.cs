using System.Reflection;
using Defra.WasteObligations.Api.Authentication;
using Defra.WasteObligations.Api.Consumers;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Endpoints;
using Defra.WasteObligations.Api.Endpoints.ComplianceDeclarations;
using Defra.WasteObligations.Api.Endpoints.OpenApi;
using Defra.WasteObligations.Api.Endpoints.Organisations.Prns;
using Defra.WasteObligations.Api.Extensions;
using Defra.WasteObligations.Api.Schemas;
using Defra.WasteObligations.Api.Services;
using Defra.WasteObligations.Api.Services.AccountBackend;
using Defra.WasteObligations.Api.Services.GovukNotify;
using Defra.WasteObligations.Api.Services.PrnCommonBackend;
using Defra.WasteObligations.Api.Services.WasteOrganisations;
using Defra.WasteObligations.Api.Utils;
using Defra.WasteObligations.Api.Utils.ErrorHandling;
using Defra.WasteObligations.Api.Utils.Health;
using Defra.WasteObligations.Api.Utils.Http;
using Defra.WasteObligations.Api.Utils.Logging;
using Defra.WasteObligations.Api.Utils.Metrics;
using Defra.WasteObligations.Api.Utils.Security;
using Defra.WasteObligations.AuditEvents;
using Defra.WasteObligations.AuditEvents.Analytics;
using Elastic.CommonSchema.Serilog;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console(new EcsTextFormatter()).CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    var integrationTest = args.Contains("--integrationTest=true");
    var openApiBuild = Assembly.GetEntryAssembly().IsProjectBuildGeneratingOpenApi();

    builder.Configuration.AddEnvironmentVariables();
    builder.Services.AddCustomTrustStore(); // This must happen before Mongo and Http client connections
    builder.ConfigureLoggingAndTracing(integrationTest);
    builder.Services.Configure<RouteHandlerOptions>(o =>
    {
        // Without this, bad request detail will only be thrown in DEVELOPMENT mode
        o.ThrowOnBadRequest = true;
    });
    builder.Services.AddProblemDetails();
    builder.Services.AddHealth();
    builder.Services.AddOpenApi(options =>
    {
        options.AddSchemaTransformer<PossibleValueSchemaTransformer>();
        options.AddDocumentTransformer<OpenApiDocumentTransformer>();
        options.AddOperationTransformer<SearchComplianceDeclarationsOperationTransformer>();
        options.AddOperationTransformer<SearchPrnsOperationTransformer>();
    });
    builder.Services.AddAuthenticationAuthorization();
    builder.Services.AddRequestMetrics();
    builder.Services.AddDbContext(builder.Configuration, integrationTest || openApiBuild);
    builder.Services.AddValidation();
    builder.Services.AddTransient<ProxyHttpMessageHandler>();
    builder.Services.AddPrnCommonBackendService();
    builder.Services.AddAccountBackendService();
    builder.Services.AddWasteOrganisationsService();
    builder.Services.AddGovukNotify();
    builder.Services.AddAuditEvents(builder.Configuration, !integrationTest && !openApiBuild);
    builder.Services.AddConsumers(builder.Configuration, !integrationTest && !openApiBuild);
    builder.Services.AddSingleton<IEntityJsonSchemaProvider, EmbeddedEntityJsonSchemaProvider>();
    builder.Services.AddTransient<IComplianceDeclarationService, ComplianceDeclarationService>();
    builder.Services.AddTransient<IEmailService, EmailService>();

    var app = builder.Build();

    app.UseErrorHandling();
    app.UseHstsUnconditionally();
    app.UseHeaderPropagation();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapHealth();
    app.UseRequestMetrics(openApiBuild);
    app.MapOpenApi("/documentation/openapi/{documentName}.json");
    app.UseReDoc(options =>
    {
        options.RoutePrefix = "documentation";
        options.SpecUrl = "/documentation/openapi/v1.json";
    });

    app.MapApiEndpoints();

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application start-up failed");
}
finally
{
    await Log.CloseAndFlushAsync();
}
