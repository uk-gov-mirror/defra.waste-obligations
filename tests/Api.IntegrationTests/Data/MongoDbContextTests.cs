using System.Net;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;

namespace Defra.WasteObligations.Api.IntegrationTests.Data;

[Trait("Category", "IntegrationTests")]
[Collection("Integration Tests")]
public class MongoDbContextTests
{
    private const string DatabaseName = "waste-obligations-mongo-db-context-tests";

    private readonly IMongoDatabase _database;

    public MongoDbContextTests()
    {
        var settings = MongoClientSettings.FromConnectionString(
            "mongodb://127.0.0.1:27017/?replicaSet=rs0&directConnection=true&readPreference=secondaryPreferred"
        );
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
        settings.ConnectTimeout = TimeSpan.FromSeconds(5);
        settings.SocketTimeout = TimeSpan.FromSeconds(5);

        _database = new MongoClient(settings).GetDatabase(DatabaseName);
    }

    [Fact]
    public async Task ExecuteTransaction_WhenWriteConflictOccurs_ShouldRetryAndLogWarning()
    {
        const int expectedResult = 42;
        const string transactionName = "write conflict test";
        var logger = new RecordingLogger<MongoDbContext>();
        var subject = CreateSubject(logger, 1);
        var writeConflict = CreateWriteConflictException();
        var invocationCount = 0;

        var result = await subject.ExecuteTransaction(
            (_, _) =>
            {
                invocationCount++;

                return invocationCount == 1 ? Task.FromException<int>(writeConflict) : Task.FromResult(expectedResult);
            },
            transactionName,
            TestContext.Current.CancellationToken
        );

        result.Should().Be(expectedResult);
        invocationCount.Should().Be(2);
        logger
            .Entries.Should()
            .ContainSingle(x =>
                x.Level == LogLevel.Warning
                && x.Exception == writeConflict
                && x.Message.StartsWith(
                    "Retrying MongoDB transaction 'write conflict test' after a retryable transaction write error. Retry 1 of 1 in "
                )
            );
    }

    [Fact]
    public async Task ExecuteTransaction_WhenTransientTransactionErrorOccurs_ShouldRetry()
    {
        const int expectedResult = 42;
        var logger = new RecordingLogger<MongoDbContext>();
        var subject = CreateSubject(logger, 1);
        var transientTransactionError = new MongoException("Transient transaction error");
        transientTransactionError.AddErrorLabel("TransientTransactionError");
        var invocationCount = 0;

        var result = await subject.ExecuteTransaction(
            (_, _) =>
            {
                invocationCount++;

                return invocationCount == 1
                    ? Task.FromException<int>(transientTransactionError)
                    : Task.FromResult(expectedResult);
            },
            "transient transaction test",
            TestContext.Current.CancellationToken
        );

        result.Should().Be(expectedResult);
        invocationCount.Should().Be(2);
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteTransaction_WhenRetryLimitIsReached_ShouldRethrowTheWriteConflict()
    {
        var logger = new RecordingLogger<MongoDbContext>();
        var subject = CreateSubject(logger, 1);
        var writeConflict = CreateWriteConflictException();
        var invocationCount = 0;
        var act = async () =>
            await subject.ExecuteTransaction<int>(
                (_, _) =>
                {
                    invocationCount++;

                    return Task.FromException<int>(writeConflict);
                },
                "retry limit test",
                TestContext.Current.CancellationToken
            );

        await act.Should().ThrowAsync<MongoCommandException>();

        invocationCount.Should().Be(2);
        logger.Entries.Should().ContainSingle(x => x.Exception == writeConflict);
    }

    [Fact]
    public async Task ExecuteTransaction_WhenRetryDelayExceedsTimeout_ShouldThrowTimeoutException()
    {
        const int transactionTimeoutSeconds = 1;
        const string transactionName = "retry timeout test";
        var logger = new RecordingLogger<MongoDbContext>();
        var subject = CreateSubject(logger, 6, transactionTimeoutSeconds);
        var writeConflict = CreateWriteConflictException();
        var invocationCount = 0;
        var act = async () =>
            await subject.ExecuteTransaction<int>(
                (_, _) =>
                {
                    invocationCount++;

                    return Task.FromException<int>(writeConflict);
                },
                transactionName,
                TestContext.Current.CancellationToken
            );

        await act.Should()
            .ThrowAsync<TimeoutException>()
            .WithMessage(
                $"MongoDB transaction '{transactionName}' timed out after {transactionTimeoutSeconds} seconds"
            );

        invocationCount.Should().BeGreaterThanOrEqualTo(6);
        logger
            .Entries.Should()
            .Contain(x =>
                x.Level == LogLevel.Warning
                && x.Message
                    == $"MongoDB transaction '{transactionName}' timed out after {transactionTimeoutSeconds} seconds"
                && x.Exception is OperationCanceledException
            );
    }

    [Fact]
    public async Task ExecuteTransaction_WhenErrorIsNotRetryable_ShouldRethrowWithoutRetrying()
    {
        var logger = new RecordingLogger<MongoDbContext>();
        var subject = CreateSubject(logger, 1);
        var nonRetryableError = new MongoException("Non-retryable error");
        var invocationCount = 0;
        var act = async () =>
            await subject.ExecuteTransaction<int>(
                (_, _) =>
                {
                    invocationCount++;

                    return Task.FromException<int>(nonRetryableError);
                },
                "non-retryable error test",
                TestContext.Current.CancellationToken
            );

        await act.Should().ThrowAsync<MongoException>();

        invocationCount.Should().Be(1);
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void OrganisationObligationSummary_ShouldUseItsEntityCollectionName()
    {
        var subject = CreateSubject(new RecordingLogger<MongoDbContext>(), 1);

        subject
            .OrganisationObligationSummaries.CollectionNamespace.CollectionName.Should()
            .Be(nameof(OrganisationObligationSummary));
    }

    private MongoDbContext CreateSubject(
        RecordingLogger<MongoDbContext> logger,
        int retryCount,
        int transactionTimeoutSeconds = 5
    ) =>
        new(
            _database,
            Options.Create(
                new MongoDbOptions
                {
                    TransactionTimeoutSeconds = transactionTimeoutSeconds,
                    TransactionWriteConflictRetryCount = retryCount,
                }
            ),
            logger
        );

    private static MongoCommandException CreateWriteConflictException() =>
        new(
            new ConnectionId(new ServerId(new ClusterId(), new DnsEndPoint("localhost", 27017))),
            "Write conflict",
            new BsonDocument("update", "complianceDeclarations"),
            new BsonDocument("code", 112)
        );
}
