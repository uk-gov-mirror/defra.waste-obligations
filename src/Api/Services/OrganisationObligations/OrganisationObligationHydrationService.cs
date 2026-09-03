using System.Security.Cryptography;
using System.Text;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Services.PrnCommonBackend;
using Defra.WasteObligations.Api.Utils.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Defra.WasteObligations.Api.Services.OrganisationObligations;

public class OrganisationObligationHydrationService(
    IDbContext dbContext,
    IOrganisationObligationSource obligationSource,
    IOrganisationObligationRequestPacer requestPacer,
    IOrganisationObligationHydrationMetrics metrics,
    IOptions<OrganisationObligationHydrationOptions> options,
    TimeProvider timeProvider,
    ILogger<OrganisationObligationHydrationService> logger
) : IOrganisationObligationHydrationService
{
    private const int MaximumFailureLength = 1000;

    public async Task<int> EnqueueNewEligible(int obligationYear, CancellationToken cancellationToken)
    {
        var organisationIds = await GetEligibleOrganisationIds(obligationYear, cancellationToken);
        var result = await EnqueueNewEligible(organisationIds, obligationYear, cancellationToken);

        return result;
    }

    public async Task<int> HydrateDue(int obligationYear, CancellationToken cancellationToken)
    {
        var organisationIds = await GetEligibleOrganisationIds(obligationYear, cancellationToken);
        await RemoveInactiveWork(organisationIds, obligationYear, cancellationToken);
        await EnqueueNewEligible(organisationIds, obligationYear, cancellationToken);
        var utcNow = timeProvider.GetUtcNowWithoutMicroseconds();
        var work = await dbContext
            .OrganisationObligationSummaries.Find(x =>
                x.ObligationYear == obligationYear && x.IsHydrationActive && x.NextRefreshAt <= utcNow
            )
            .SortBy(x => x.Priority)
            .ThenBy(x => x.NextRefreshAt)
            .Limit(options.Value.BatchSize)
            .ToListAsync(cancellationToken);
        var processedCount = 0;
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = options.Value.MaxConcurrentRequests,
        };

        await Parallel.ForEachAsync(
            work,
            parallelOptions,
            async (item, token) =>
            {
                await Hydrate(item, token);
                Interlocked.Increment(ref processedCount);
            }
        );
        await ObserveStaleness(obligationYear, utcNow, cancellationToken);

        return processedCount;
    }

    public async Task<int> EnqueueReconciliation(
        int obligationYear,
        DateTime reconciliationSince,
        CancellationToken cancellationToken
    )
    {
        var organisationIds = await GetEligibleOrganisationIds(obligationYear, cancellationToken);
        if (organisationIds.Length == 0)
            return 0;

        var utcNow = timeProvider.GetUtcNowWithoutMicroseconds();
        var filter = Builders<OrganisationObligationSummary>.Filter.And(
            Builders<OrganisationObligationSummary>.Filter.Eq(x => x.ObligationYear, obligationYear),
            Builders<OrganisationObligationSummary>.Filter.In(x => x.OrganisationId, organisationIds),
            Builders<OrganisationObligationSummary>.Filter.Eq(x => x.IsHydrationActive, true),
            Builders<OrganisationObligationSummary>.Filter.Eq(
                x => x.Priority,
                OrganisationObligationHydrationPriority.ScheduledRefresh
            ),
            Builders<OrganisationObligationSummary>.Filter.Or(
                Builders<OrganisationObligationSummary>.Filter.Eq(x => x.LastSuccessfulReadAt, null),
                Builders<OrganisationObligationSummary>.Filter.Lt(x => x.LastSuccessfulReadAt, reconciliationSince)
            )
        );
        var update = Builders<OrganisationObligationSummary>
            .Update.Set(x => x.Priority, OrganisationObligationHydrationPriority.Reconciliation)
            .Set(x => x.NextRefreshAt, utcNow);
        var result = await dbContext.OrganisationObligationSummaries.UpdateManyAsync(
            filter,
            update,
            cancellationToken: cancellationToken
        );

        return (int)result.ModifiedCount;
    }

    private async Task<Guid[]> GetEligibleOrganisationIds(int obligationYear, CancellationToken cancellationToken)
    {
        var snapshot = await dbContext
            .OrganisationEligibilitySnapshots.Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleOrDefaultAsync(cancellationToken);
        if (snapshot?.ActiveGeneration is null)
            return [];

        var eligibleOrganisationIds = await dbContext
            .OrganisationComplianceDeclarationEligibilities.Find(x =>
                x.Generation == snapshot.ActiveGeneration
                && x.ObligationYear == obligationYear
                && x.RegistrationStatus == OrganisationRegistrationStatus.Registered
                && x.ReferenceNumberResolutionState == OrganisationReferenceNumberResolutionState.Resolved
            )
            .Project(x => x.OrganisationId)
            .ToListAsync(cancellationToken);

        return eligibleOrganisationIds.Distinct().ToArray();
    }

    private async Task<int> EnqueueNewEligible(
        Guid[] organisationIds,
        int obligationYear,
        CancellationToken cancellationToken
    )
    {
        if (organisationIds.Length == 0)
            return 0;

        var utcNow = timeProvider.GetUtcNowWithoutMicroseconds();
        var work = organisationIds
            .Select(organisationId => new UpdateOneModel<OrganisationObligationSummary>(
                Builders<OrganisationObligationSummary>.Filter.And(
                    Builders<OrganisationObligationSummary>.Filter.Eq(x => x.OrganisationId, organisationId),
                    Builders<OrganisationObligationSummary>.Filter.Eq(x => x.ObligationYear, obligationYear)
                ),
                Builders<OrganisationObligationSummary>
                    .Update.SetOnInsert(x => x.OrganisationId, organisationId)
                    .SetOnInsert(x => x.ObligationYear, obligationYear)
                    .SetOnInsert(x => x.Priority, OrganisationObligationHydrationPriority.NewEligible)
                    .SetOnInsert(x => x.NextRefreshAt, utcNow)
                    .SetOnInsert(x => x.AttemptCount, 0)
                    .SetOnInsert(x => x.RequestedAt, utcNow)
                    .SetOnInsert(x => x.RefreshState, OrganisationObligationRefreshState.Pending)
                    .Set(x => x.IsHydrationActive, true)
            )
            {
                IsUpsert = true,
            })
            .ToArray();

        var result = await dbContext.OrganisationObligationSummaries.BulkWriteAsync(
            work,
            cancellationToken: cancellationToken
        );

        return result.Upserts.Count;
    }

    private async Task RemoveInactiveWork(
        Guid[] organisationIds,
        int obligationYear,
        CancellationToken cancellationToken
    )
    {
        var filter = Builders<OrganisationObligationSummary>.Filter.And(
            Builders<OrganisationObligationSummary>.Filter.Eq(x => x.ObligationYear, obligationYear),
            Builders<OrganisationObligationSummary>.Filter.Eq(x => x.IsHydrationActive, true)
        );
        if (organisationIds.Length > 0)
        {
            filter &= Builders<OrganisationObligationSummary>.Filter.Nin(x => x.OrganisationId, organisationIds);
        }

        await dbContext.OrganisationObligationSummaries.UpdateManyAsync(
            filter,
            Builders<OrganisationObligationSummary>.Update.Set(x => x.IsHydrationActive, false),
            cancellationToken: cancellationToken
        );
    }

    private async Task Hydrate(OrganisationObligationSummary work, CancellationToken cancellationToken)
    {
        try
        {
            await requestPacer.Wait(cancellationToken);
            var obligations = await obligationSource.ReadObligations(
                work.OrganisationId,
                work.ObligationYear,
                cancellationToken
            );
            var summaryMetrics = OrganisationObligationSummaryMapper.Map(
                work.OrganisationId,
                work.ObligationYear,
                obligations
            );
            var utcNow = timeProvider.GetUtcNowWithoutMicroseconds();
            var nextRefreshAt = NextRefreshAt(work.OrganisationId, work.ObligationYear, utcNow);
            var summary = work with
            {
                ObligationCount = summaryMetrics.ObligationCount,
                TotalAcceptedTonnage = summaryMetrics.TotalAcceptedTonnage,
                TotalObligatedTonnage = summaryMetrics.TotalObligatedTonnage,
                RecyclingObligationsMet = summaryMetrics.RecyclingObligationsMet,
                ObligationCoveragePercentage = summaryMetrics.ObligationCoveragePercentage,
                SourceFingerprint = summaryMetrics.SourceFingerprint,
                LastSuccessfulReadAt = utcNow,
                LastAttemptedAt = utcNow,
                NextRefreshAt = nextRefreshAt,
                RefreshState = OrganisationObligationRefreshState.Ready,
                AttemptCount = 0,
                LastFailure = null,
                Priority = OrganisationObligationHydrationPriority.ScheduledRefresh,
                IsHydrationActive = true,
            };

            await Persist(summary, cancellationToken);
            metrics.Succeeded();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RecordFailure(work, exception, cancellationToken);
            metrics.Failed();
        }
    }

    private async Task Persist(OrganisationObligationSummary summary, CancellationToken cancellationToken)
    {
        await dbContext.ExecuteTransaction(
            async (session, token) =>
            {
                await dbContext.OrganisationObligationSummaries.ReplaceOneAsync(
                    session,
                    x => x.OrganisationId == summary.OrganisationId && x.ObligationYear == summary.ObligationYear,
                    summary,
                    new ReplaceOptions { IsUpsert = true },
                    token
                );
                var activeGeneration = await dbContext
                    .OrganisationEligibilitySnapshots.Find(
                        session,
                        x => x.Id == OrganisationEligibilitySnapshot.SnapshotId
                    )
                    .Project(x => x.ActiveGeneration)
                    .SingleOrDefaultAsync(token);
                if (activeGeneration is not null)
                {
                    await dbContext.OrganisationComplianceDeclarationEligibilities.UpdateManyAsync(
                        session,
                        x =>
                            x.Generation == activeGeneration
                            && x.OrganisationId == summary.OrganisationId
                            && x.ObligationYear == summary.ObligationYear,
                        Builders<OrganisationComplianceDeclarationEligibility>
                            .Update.Set(x => x.RecyclingObligationsMet, summary.RecyclingObligationsMet)
                            .Set(x => x.ObligationCoveragePercentage, summary.ObligationCoveragePercentage ?? 0),
                        cancellationToken: token
                    );
                }
                await OrganisationEligibilitySnapshotState.IncrementMaterialisedStateVersion(dbContext, session, token);

                return true;
            },
            "persist organisation obligation hydration result",
            cancellationToken
        );
    }

    private async Task RecordFailure(
        OrganisationObligationSummary work,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        var utcNow = timeProvider.GetUtcNowWithoutMicroseconds();
        var attemptCount = work.AttemptCount + 1;
        var nextAttemptAt = utcNow.Add(RetryDelay(attemptCount));
        var failure =
            exception.Message.Length > MaximumFailureLength
                ? exception.Message[..MaximumFailureLength]
                : exception.Message;
        var summary = work with
        {
            LastAttemptedAt = utcNow,
            NextRefreshAt = nextAttemptAt,
            RefreshState = OrganisationObligationRefreshState.Failed,
            AttemptCount = attemptCount,
            LastFailure = failure,
            Priority = OrganisationObligationHydrationPriority.Retry,
            IsHydrationActive = true,
        };

        await Persist(summary, cancellationToken);
    }

    private TimeSpan RetryDelay(int attemptCount)
    {
        var multiplier = 1L << Math.Min(attemptCount - 1, 20);
        var retryTicks = Math.Min(
            options.Value.InitialRetryDelay.Ticks * multiplier,
            options.Value.MaximumRetryDelay.Ticks
        );

        return TimeSpan.FromTicks(retryTicks);
    }

    private async Task ObserveStaleness(int obligationYear, DateTime utcNow, CancellationToken cancellationToken)
    {
        var staleBefore = utcNow.Subtract(options.Value.MaximumSummaryStaleness);
        var staleSummaryTimes = await dbContext
            .OrganisationObligationSummaries.Find(x =>
                x.ObligationYear == obligationYear
                && x.IsHydrationActive
                && (
                    (x.LastSuccessfulReadAt != null && x.LastSuccessfulReadAt < staleBefore)
                    || (x.LastSuccessfulReadAt == null && x.RequestedAt < staleBefore)
                )
            )
            .Project(x => x.LastSuccessfulReadAt ?? x.RequestedAt)
            .ToListAsync(cancellationToken);
        var oldestStaleSummaryAgeSeconds =
            staleSummaryTimes.Count > 0 ? (utcNow - staleSummaryTimes.Min()).TotalSeconds : 0;

        metrics.StalenessObserved(staleSummaryTimes.Count, oldestStaleSummaryAgeSeconds);
        if (staleSummaryTimes.Count > 0)
        {
            logger.LogWarning(
                "Organisation obligation hydration has {StaleSummaryCount} active summaries older than {MaximumSummaryStaleness}. The oldest is {OldestStaleSummaryAgeSeconds} seconds old for obligation year {ObligationYear}",
                staleSummaryTimes.Count,
                options.Value.MaximumSummaryStaleness,
                oldestStaleSummaryAgeSeconds,
                obligationYear
            );
        }
    }

    private DateTime NextRefreshAt(Guid organisationId, int obligationYear, DateTime utcNow)
    {
        var intervalTicks = options.Value.RefreshInterval.Ticks;
        var currentIntervalStart = utcNow.Ticks - utcNow.Ticks % intervalTicks;
        var fingerprint = SHA256.HashData(Encoding.UTF8.GetBytes($"{organisationId:N}|{obligationYear}"));
        var slot = BitConverter.ToUInt64(fingerprint) % (ulong)intervalTicks;
        var nextRefreshAt = new DateTime(currentIntervalStart + (long)slot, DateTimeKind.Utc);

        return nextRefreshAt <= utcNow ? nextRefreshAt.AddTicks(intervalTicks) : nextRefreshAt;
    }
}
