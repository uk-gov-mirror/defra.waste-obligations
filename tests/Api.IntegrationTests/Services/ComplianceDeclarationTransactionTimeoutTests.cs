using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.IntegrationTests.Infrastructure;
using Defra.WasteObligations.Api.Services;
using Defra.WasteObligations.Api.Utils.Logging;
using Defra.WasteObligations.Api.Utils.Metrics;
using Defra.WasteObligations.AuditEvents;
using Defra.WasteObligations.Testing;
using Defra.WasteObligations.Testing.Fixtures.Entities;
using Microsoft.AspNetCore.HeaderPropagation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using NSubstitute;

namespace Defra.WasteObligations.Api.IntegrationTests.Services;

[Trait("Category", "IntegrationTests")]
[Collection("Integration Tests")]
public class ComplianceDeclarationTransactionTimeoutTests : IAsyncLifetime
{
    private const string DatabaseName = "waste-obligations-transaction-timeout-tests";
    private static readonly MongoQueryProfileAllowance UnmigratedDatabaseQueryAllowance = new(
        "query",
        $"{DatabaseName}.ComplianceDeclaration",
        "{$and:[{$or:[{status:?}]},{obligationYear:?},{organisation._id:?},{organisation.registrationType:?}]}",
        "MO-485",
        "This transaction-timeout test deliberately uses an unmigrated isolated database."
    );

    private readonly MongoClient _mongoClient;
    private readonly IMongoDatabase _database;
    private MongoQueryProfiler? _mongoQueryProfiler;

    public ComplianceDeclarationTransactionTimeoutTests()
    {
        var settings = MongoClientSettings.FromConnectionString(
            "mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true&readPreference=secondaryPreferred"
        );
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);
        settings.SocketTimeout = TimeSpan.FromSeconds(5);
        settings.ApplicationName = MongoQueryProfiler.IntegrationTestApplicationName;

        _mongoClient = new MongoClient(settings);
        _database = _mongoClient.GetDatabase(DatabaseName);
    }

    [Fact]
    public async Task Create_WhenTransactionExceedsTimeout_ShouldRollbackAndLogTimeout()
    {
        var complianceDeclarationMetrics = Substitute.For<IComplianceDeclarationMetrics>();
        var mongoDbLogger = new RecordingLogger<MongoDbContext>();
        var dbContext = new MongoDbContext(
            _database,
            Options.Create(new MongoDbOptions { TransactionTimeoutSeconds = 1 }),
            mongoDbLogger
        );
        var subject = new ComplianceDeclarationService(
            dbContext,
            Substitute.For<ILogger<ComplianceDeclarationService>>(),
            TimeProvider.System,
            new WaitingAuditEventService(),
            complianceDeclarationMetrics,
            new TraceIdReader(
                new HeaderPropagationValues(),
                Options.Create(new TraceHeader { Name = "x-cdp-request-id" })
            ),
            new UnsubmittedEligibilityVisibilityService(dbContext)
        );
        var complianceDeclaration = ComplianceDeclarationFixture.Default().Create();
        var act = async () => await subject.Create(complianceDeclaration, TestContext.Current.CancellationToken);

        await act.Should()
            .ThrowAsync<TimeoutException>()
            .WithMessage(
                $"MongoDB transaction 'compliance declaration create {complianceDeclaration.Id}' timed out after 1 seconds"
            );

        var retrieved = await subject.Read(complianceDeclaration.Id.ToString(), TestContext.Current.CancellationToken);
        retrieved.Should().BeNull();
        mongoDbLogger
            .Entries.Should()
            .ContainSingle(x =>
                x.Level == LogLevel.Warning
                && x.Message
                    == $"MongoDB transaction 'compliance declaration create {complianceDeclaration.Id}' timed out after 1 seconds"
                && x.Exception is OperationCanceledException
            );
        complianceDeclarationMetrics.DidNotReceive().Created();
    }

    public async ValueTask InitializeAsync()
    {
        await _mongoClient.DropDatabaseAsync(DatabaseName, TestContext.Current.CancellationToken);
        _mongoQueryProfiler = await MongoQueryProfiler.Start(
            _database,
            [MongoQueryProfiler.IntegrationTestApplicationName],
            TestContext.Current.CancellationToken
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_mongoQueryProfiler is not null)
        {
            var profile = await _mongoQueryProfiler.Stop(CancellationToken.None);
            profile.AssertIndexesUsed([UnmigratedDatabaseQueryAllowance]);
        }

        await _mongoClient.DropDatabaseAsync(DatabaseName, CancellationToken.None);
        GC.SuppressFinalize(this);
    }

    private class WaitingAuditEventService : IAuditEventService
    {
        public async Task RecordEvent(
            IClientSessionHandle session,
            AuditEventRequest auditEvent,
            CancellationToken cancellationToken
        ) => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
