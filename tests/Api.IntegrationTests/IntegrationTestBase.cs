using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Authentication;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.IntegrationTests.Infrastructure;
using Defra.WasteObligations.AuditEvents.Data;
using Defra.WasteObligations.AuditEvents.Entities;
using Defra.WasteObligations.Testing;
using MongoDB.Driver;
using ServiceCollectionExtensions = Defra.WasteObligations.Api.Data.ServiceCollectionExtensions;

namespace Defra.WasteObligations.Api.IntegrationTests;

[Trait("Category", "IntegrationTests")]
[Collection("Integration Tests")]
public abstract class IntegrationTestBase : IAsyncLifetime
{
    protected const string TraceHeaderName = "x-cdp-request-id";
    protected const string TraceId = "trace-id-1";
    protected const string AnalyticsEventsQueueUrl =
        "http://localhost:4566/000000000000/waste_obligations_analytics_events_queue";

    private const string ContentEncodingHeader = "Content-Encoding";
    private const string ContentTypeHeader = "Content-Type";
    private const string JsonContentType = "application/json";
    private const string ServiceUrl = "http://localhost:4566";

    public required WireMockContext WireMockContext;

    public required IMongoCollection<ComplianceDeclaration> ComplianceDeclarations { get; set; }
    public required IMongoCollection<AuditEventCounter> AuditEventCounters { get; set; }
    public required IMongoCollection<AuditEvent> AuditEvents { get; set; }
    public required IMongoCollection<AuditEventDispatchLease> AuditEventDispatchLeases { get; set; }
    public required IMongoCollection<OrganisationComplianceDeclarationEligibility> OrganisationComplianceDeclarationEligibilities { get; set; }
    public required IMongoCollection<OrganisationEligibilitySnapshot> OrganisationEligibilitySnapshots { get; set; }
    public required IMongoCollection<BackgroundWorkerLease> OrganisationWorkerLeases { get; set; }
    public required IMongoCollection<OrganisationObligationSummary> OrganisationObligationSummaries { get; set; }

    [ModuleInitializer]
    public static void RegisterMongoConventions() => ServiceCollectionExtensions.RegisterConventions();

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    public async ValueTask InitializeAsync()
    {
        WireMockContext = new WireMockContext();

        await WireMockContext.InitializeAsync();

        ComplianceDeclarations = GetMongoCollection<ComplianceDeclaration>();
        AuditEventCounters = GetMongoCollection<AuditEventCounter>(AuditEventDbContext.AuditEventCounterCollectionName);
        AuditEvents = GetMongoCollection<AuditEvent>();
        AuditEventDispatchLeases = GetMongoCollection<AuditEventDispatchLease>(
            AuditEventDbContext.AuditEventDispatchLeaseCollectionName
        );
        OrganisationComplianceDeclarationEligibilities =
            GetMongoCollection<OrganisationComplianceDeclarationEligibility>();
        OrganisationEligibilitySnapshots = GetMongoCollection<OrganisationEligibilitySnapshot>();
        OrganisationWorkerLeases = GetMongoCollection<BackgroundWorkerLease>(BackgroundWorkerLease.CollectionName);
        OrganisationObligationSummaries = GetMongoCollection<OrganisationObligationSummary>();

        await DeleteMany(ComplianceDeclarations);
        await DeleteMany(AuditEventCounters);
        await DeleteMany(AuditEvents);
        await DeleteMany(AuditEventDispatchLeases);
        await DeleteMany(OrganisationComplianceDeclarationEligibilities);
        await DeleteMany(OrganisationEligibilitySnapshots);
        await DeleteMany(OrganisationWorkerLeases);
        await DeleteMany(OrganisationObligationSummaries);

        using var sqsClient = CreateSqsClient();
        await DrainAnalyticsEventsQueue(sqsClient);
    }

    protected static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("http://localhost:8080") };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            JwtAuthenticationHandler.SchemeName,
            // See compose.yml for configuration of IntegrationTest client
            GenerateJwt("IntegrationTest")
        );

        return client;
    }

    protected static IAmazonSQS CreateSqsClient()
    {
        var config = new AmazonSQSConfig { ServiceURL = ServiceUrl, AuthenticationRegion = "eu-west-2" };
        var credentials = new BasicAWSCredentials("test", "test");

        return new AmazonSQSClient(credentials, config);
    }

    protected static Task WaitForAsync(Func<Task> assertion, double? timeout = null, TimeSpan? delay = null) =>
        AsyncWaiter.WaitForAsync(assertion, timeout, delay);

    protected static async Task DrainAnalyticsEventsQueue(IAmazonSQS sqsClient)
    {
        while (true)
        {
            var response = await sqsClient.ReceiveMessageAsync(
                new ReceiveMessageRequest
                {
                    QueueUrl = AnalyticsEventsQueueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 0,
                },
                TestContext.Current.CancellationToken
            );

            if (response.Messages is not { Count: > 0 })
                return;

            foreach (var message in response.Messages)
            {
                await sqsClient.DeleteMessageAsync(
                    AnalyticsEventsQueueUrl,
                    message.ReceiptHandle,
                    TestContext.Current.CancellationToken
                );
            }
        }
    }

    protected static async Task<JsonDocument> ReceiveAnalyticsEventsQueueJsonMessage(
        IAmazonSQS sqsClient,
        Func<JsonElement, bool>? match = null
    )
    {
        JsonDocument? deserializedMessage = null;

        await AsyncWaiter.WaitForAsync(
            async () =>
            {
                var response = await sqsClient.ReceiveMessageAsync(
                    new ReceiveMessageRequest
                    {
                        QueueUrl = AnalyticsEventsQueueUrl,
                        MaxNumberOfMessages = 10,
                        MessageAttributeNames = ["All"],
                        WaitTimeSeconds = 1,
                    },
                    TestContext.Current.CancellationToken
                );

                if (response.Messages is not { Count: > 0 })
                    throw new InvalidOperationException("Expected analytics event messages.");

                foreach (var message in response.Messages)
                {
                    message.MessageAttributes.Should().ContainKey(ContentTypeHeader);
                    message.MessageAttributes[ContentTypeHeader].StringValue.Should().Be(JsonContentType);
                    message.MessageAttributes.Should().NotContainKey(ContentEncodingHeader);

                    using var candidate = JsonSerializer.Deserialize<JsonDocument>(message.Body);
                    candidate.Should().NotBeNull();

                    if (match is not null && !match(candidate.RootElement))
                    {
                        await sqsClient.DeleteMessageAsync(
                            AnalyticsEventsQueueUrl,
                            message.ReceiptHandle,
                            TestContext.Current.CancellationToken
                        );

                        continue;
                    }

                    await sqsClient.DeleteMessageAsync(
                        AnalyticsEventsQueueUrl,
                        message.ReceiptHandle,
                        TestContext.Current.CancellationToken
                    );

                    deserializedMessage = JsonDocument.Parse(message.Body);

                    return;
                }

                throw new InvalidOperationException("Expected a matching analytics event message.");
            },
            timeout: 10,
            delay: TimeSpan.FromMilliseconds(100)
        );

        deserializedMessage.Should().NotBeNull();

        return deserializedMessage;
    }

    protected static async Task AssertAnalyticsEventQueued(
        IAmazonSQS sqsClient,
        string complianceDeclarationId,
        string operation,
        string eventType,
        string? deletedReason = null
    )
    {
        var expectedEntityId = $"compliance_declaration_{complianceDeclarationId}";

        using var deserializedMessage = await ReceiveAnalyticsEventsQueueJsonMessage(
            sqsClient,
            root =>
                root.GetProperty("entityId").GetString() == expectedEntityId
                && root.GetProperty("operation").GetString() == operation
                && root.GetProperty("eventType").GetString() == eventType
        );
        var root = deserializedMessage.RootElement;

        root.GetProperty("entityId").GetString().Should().Be(expectedEntityId);
        root.GetProperty("operation").GetString().Should().Be(operation);
        root.GetProperty("eventType").GetString().Should().Be(eventType);
        var deletedReasonProperty = root.GetProperty("deletedReason");

        if (deletedReason is null)
        {
            deletedReasonProperty.ValueKind.Should().Be(JsonValueKind.Null);
        }
        else
        {
            deletedReasonProperty.GetString().Should().Be(deletedReason);
        }
    }

    protected static Func<JsonElement, bool> MatchAnalyticsEvent(
        string complianceDeclarationId,
        string operation,
        string eventType
    )
    {
        var expectedEntityId = $"compliance_declaration_{complianceDeclarationId}";

        return root =>
            root.GetProperty("entityId").GetString() == expectedEntityId
            && root.GetProperty("operation").GetString() == operation
            && root.GetProperty("eventType").GetString() == eventType;
    }

    private static string GenerateJwt(string clientId)
    {
        var claims = new[] { new Claim(Claims.ClientId, clientId) };

        return Jwt.GenerateJwt(claims);
    }

    protected static IMongoDatabase GetMongoDatabase()
    {
        var settings = MongoClientSettings.FromConnectionString(
            "mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true&readPreference=secondaryPreferred"
        );
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);
        settings.SocketTimeout = TimeSpan.FromSeconds(5);
        settings.ApplicationName = MongoQueryProfiler.IntegrationTestApplicationName;

        return new MongoClient(settings).GetDatabase("waste-obligations");
    }

    private static IMongoCollection<T> GetMongoCollection<T>() => GetMongoCollection<T>(typeof(T).Name);

    private static IMongoCollection<T> GetMongoCollection<T>(string collectionName) =>
        GetMongoDatabase().GetCollection<T>(collectionName);

    private static async Task DeleteMany<T>(IMongoCollection<T> collection) =>
        await collection.DeleteManyAsync(FilterDefinition<T>.Empty, TestContext.Current.CancellationToken);
}
