using Defra.WasteObligations.Api.Data.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Defra.WasteObligations.Api.Data;

public class MongoDbContext(
    IMongoDatabase database,
    IOptions<MongoDbOptions> mongoDbOptions,
    ILogger<MongoDbContext> logger
) : IDbContext
{
    private const int WriteConflictErrorCode = 112;
    private const int InitialWriteConflictRetryDelayMilliseconds = 25;
    private const int WriteConflictRetryJitterMilliseconds = 25;

    public IMongoCollection<ComplianceDeclaration> ComplianceDeclarations { get; } =
        database.GetCollection<ComplianceDeclaration>(nameof(ComplianceDeclaration));

    public IMongoCollection<OrganisationComplianceDeclarationEligibility> OrganisationComplianceDeclarationEligibilities { get; } =
        database.GetCollection<OrganisationComplianceDeclarationEligibility>(
            nameof(OrganisationComplianceDeclarationEligibility)
        );

    public IMongoCollection<OrganisationEligibilitySnapshot> OrganisationEligibilitySnapshots { get; } =
        database.GetCollection<OrganisationEligibilitySnapshot>(nameof(OrganisationEligibilitySnapshot));

    public IMongoCollection<OrganisationObligationSummary> OrganisationObligationSummaries { get; } =
        database.GetCollection<OrganisationObligationSummary>(nameof(OrganisationObligationSummary));

    public async Task<TResult> ExecuteTransaction<TResult>(
        Func<IClientSessionHandle, CancellationToken, Task<TResult>> callback,
        string transactionName,
        CancellationToken cancellationToken
    )
    {
        var transactionTimeout = TimeSpan.FromSeconds(mongoDbOptions.Value.TransactionTimeoutSeconds);
        using var timeoutCancellationTokenSource = new CancellationTokenSource(transactionTimeout);
        using var session = await StartSession(cancellationToken);

        var retryCount = 0;
        while (true)
        {
            try
            {
                // Keep the driver token tied to the caller so WithTransactionAsync can abort with a live token when this
                // service-owned budget expires. Passing the budget token to the driver would also cancel its abort command.
                return await session.WithTransactionAsync(
                    async (transactionSession, transactionCancellationToken) =>
                    {
                        using var operationCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                            transactionCancellationToken,
                            timeoutCancellationTokenSource.Token
                        );

                        try
                        {
                            return await callback(transactionSession, operationCancellationTokenSource.Token);
                        }
                        catch (OperationCanceledException exception)
                            when (timeoutCancellationTokenSource.IsCancellationRequested
                                && !cancellationToken.IsCancellationRequested
                            )
                        {
                            throw TransactionTimedOut(exception, transactionName, transactionTimeout);
                        }
                    },
                    new TransactionOptions(maxCommitTime: transactionTimeout),
                    cancellationToken
                );
            }
            catch (MongoException exception)
                when (IsRetryableTransactionWriteError(exception)
                    && retryCount < mongoDbOptions.Value.TransactionWriteConflictRetryCount
                )
            {
                var retryDelay = RetryDelay(retryCount);
                retryCount++;
                logger.LogWarning(
                    exception,
                    "Retrying MongoDB transaction '{MongoTransactionName}' after a retryable transaction write error. Retry {TransactionRetryAttempt} of {TransactionWriteConflictRetryCount} in {TransactionRetryDelayMilliseconds}ms",
                    transactionName,
                    retryCount,
                    mongoDbOptions.Value.TransactionWriteConflictRetryCount,
                    retryDelay.TotalMilliseconds
                );

                using var retryDelayCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCancellationTokenSource.Token
                );

                try
                {
                    await Task.Delay(retryDelay, retryDelayCancellationTokenSource.Token);
                }
                catch (OperationCanceledException cancellationException)
                    when (timeoutCancellationTokenSource.IsCancellationRequested
                        && !cancellationToken.IsCancellationRequested
                    )
                {
                    throw TransactionTimedOut(cancellationException, transactionName, transactionTimeout);
                }
            }
        }
    }

    private async Task<IClientSessionHandle> StartSession(CancellationToken cancellationToken)
    {
        var clientSessionOptions = new ClientSessionOptions
        {
            DefaultTransactionOptions = new TransactionOptions(readPreference: ReadPreference.Primary),
        };

        return await database.Client.StartSessionAsync(clientSessionOptions, cancellationToken: cancellationToken);
    }

    private TimeoutException TransactionTimedOut(
        OperationCanceledException exception,
        string transactionName,
        TimeSpan transactionTimeout
    )
    {
        logger.LogWarning(
            exception,
            "MongoDB transaction '{MongoTransactionName}' timed out after {MongoTransactionTimeoutSeconds} seconds",
            transactionName,
            transactionTimeout.TotalSeconds
        );

        return new TimeoutException(
            $"MongoDB transaction '{transactionName}' timed out after {transactionTimeout.TotalSeconds} seconds",
            exception
        );
    }

    private static bool IsRetryableTransactionWriteError(MongoException exception) =>
        exception.HasErrorLabel("TransientTransactionError")
        || exception is MongoCommandException { Code: WriteConflictErrorCode }
        || exception is MongoWriteException { WriteError.Code: WriteConflictErrorCode };

    private static TimeSpan RetryDelay(int retryCount)
    {
        var exponentialDelayMilliseconds = InitialWriteConflictRetryDelayMilliseconds * (1 << retryCount);
        var jitterMilliseconds = Random.Shared.Next(WriteConflictRetryJitterMilliseconds + 1);

        return TimeSpan.FromMilliseconds(exponentialDelayMilliseconds + jitterMilliseconds);
    }
}
