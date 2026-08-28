using System.Diagnostics;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Services.WasteOrganisations;
using Defra.WasteObligations.AuditEvents;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Defra.WasteObligations.Api.Services.OrganisationEligibility;

public class OrganisationEligibilityRefreshService(
    IDbContext dbContext,
    IOrganisationEligibilitySource organisationEligibilitySource,
    OrganisationReferenceResolver organisationReferenceResolver,
    IUnsubmittedEligibilityVisibilityService unsubmittedEligibilityVisibilityService,
    IOptions<OrganisationEligibilityOptions> options,
    TimeProvider timeProvider,
    ILogger<OrganisationEligibilityRefreshService> logger
) : IOrganisationEligibilityRefreshService
{
    private const int DuplicateKeyErrorCode = 11000;
    private const string ActiveGenerationChangedMessage =
        "The active organisation eligibility generation changed during refresh";

    public async Task<OrganisationEligibilityRefreshResult> Refresh(CancellationToken cancellationToken)
    {
        var source = await organisationEligibilitySource.Search(cancellationToken);
        var utcNow = timeProvider.GetUtcNowWithoutMicroseconds();
        var generation = Guid.NewGuid().ToString("N");
        var sourceRows = Mappers.ToEligibilityRows(source.Organisations, generation, utcNow);
        var activeSnapshot = await dbContext
            .OrganisationEligibilitySnapshots.Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleOrDefaultAsync(cancellationToken);
        var activeRows = await ActiveRows(activeSnapshot, cancellationToken);
        var resolvedRows = await organisationReferenceResolver.Resolve(sourceRows, activeRows, cancellationToken);
        if (
            activeSnapshot?.ActiveGeneration is null
            && resolvedRows.Any(x =>
                x.ReferenceNumberResolutionState == OrganisationReferenceNumberResolutionState.Failed
            )
        )
        {
            throw new InvalidOperationException(
                "Initial organisation eligibility generation contains failed Account reference lookups"
            );
        }

        resolvedRows = await ApplyCurrentObligationMetrics(resolvedRows, cancellationToken);
        var content = OrganisationEligibilitySnapshotContentBuilder.Create(resolvedRows);
        content = content with
        {
            Rows = await unsubmittedEligibilityVisibilityService.Apply(content.Rows, utcNow, cancellationToken),
        };

        if (
            activeSnapshot?.ActiveContentFingerprint == content.Fingerprint
            && activeSnapshot.ActiveRowCount == content.Rows.Count
        )
        {
            var retainedGenerations = UnexpiredRetainedGenerations(activeSnapshot, utcNow);
            await VerifyActiveGeneration(activeSnapshot, retainedGenerations, utcNow, cancellationToken);
            await CollectGarbage(
                activeSnapshot with
                {
                    RetainedGenerations = retainedGenerations,
                },
                utcNow,
                cancellationToken
            );

            return new OrganisationEligibilityRefreshResult
            {
                Outcome = OrganisationEligibilityRefreshOutcome.Unchanged,
                ActiveGeneration = activeSnapshot.ActiveGeneration,
                RowCount = content.Rows.Count,
                ContentFingerprint = content.Fingerprint,
            };
        }

        var snapshot = new OrganisationEligibilitySnapshot
        {
            Id = OrganisationEligibilitySnapshot.SnapshotId,
            ActiveGeneration = generation,
            ActiveContentFingerprint = content.Fingerprint,
            ActiveRowCount = content.Rows.Count,
            MaterialisedStateVersion = activeSnapshot?.MaterialisedStateVersion ?? 0,
            ActiveGenerationPromotedAt = utcNow,
            LastVerifiedAt = utcNow,
            RetainedGenerations = RetainedGenerationsAfterPromotion(activeSnapshot, utcNow),
        };
        var writeStopwatch = Stopwatch.StartNew();
        var writtenRowCount = await dbContext.ExecuteTransaction(
            async (transactionSession, transactionCancellationToken) =>
            {
                await dbContext.OrganisationComplianceDeclarationEligibilities.InsertManyAsync(
                    transactionSession,
                    content.Rows,
                    cancellationToken: transactionCancellationToken
                );
                var writtenRowCount =
                    await dbContext.OrganisationComplianceDeclarationEligibilities.CountDocumentsAsync(
                        transactionSession,
                        x => x.Generation == generation,
                        cancellationToken: transactionCancellationToken
                    );
                if (writtenRowCount != content.Rows.Count)
                {
                    throw new InvalidOperationException(
                        $"Organisation eligibility generation {generation} wrote {writtenRowCount} rows, expected {content.Rows.Count}"
                    );
                }

                await PromoteActiveGeneration(
                    transactionSession,
                    activeSnapshot,
                    snapshot,
                    transactionCancellationToken
                );

                return writtenRowCount;
            },
            $"organisation eligibility generation write {generation}",
            cancellationToken
        );
        writeStopwatch.Stop();
        logger.LogInformation(
            "Organisation eligibility generation {OrganisationEligibilityGeneration} wrote {OrganisationEligibilityDocumentCount} documents in {OrganisationEligibilityWriteDurationMilliseconds}ms",
            generation,
            writtenRowCount,
            writeStopwatch.ElapsedMilliseconds
        );
        await CollectGarbage(snapshot, utcNow, cancellationToken);

        return new OrganisationEligibilityRefreshResult
        {
            Outcome = OrganisationEligibilityRefreshOutcome.Promoted,
            ActiveGeneration = generation,
            RowCount = content.Rows.Count,
            ContentFingerprint = content.Fingerprint,
        };
    }

    private async Task VerifyActiveGeneration(
        OrganisationEligibilitySnapshot activeSnapshot,
        RetainedOrganisationEligibilityGeneration[] retainedGenerations,
        DateTime utcNow,
        CancellationToken cancellationToken
    )
    {
        var result = await dbContext.OrganisationEligibilitySnapshots.UpdateOneAsync(
            ActiveGenerationFilter(activeSnapshot.ActiveGeneration),
            Builders<OrganisationEligibilitySnapshot>
                .Update.Set(x => x.LastVerifiedAt, utcNow)
                .Set(x => x.RetainedGenerations, retainedGenerations),
            cancellationToken: cancellationToken
        );

        EnsureActiveGenerationUnchanged(result.MatchedCount);
    }

    private async Task<IReadOnlyList<OrganisationComplianceDeclarationEligibility>> ActiveRows(
        OrganisationEligibilitySnapshot? activeSnapshot,
        CancellationToken cancellationToken
    )
    {
        if (activeSnapshot?.ActiveGeneration is null)
            return [];

        return await dbContext
            .OrganisationComplianceDeclarationEligibilities.Find(x => x.Generation == activeSnapshot.ActiveGeneration)
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<OrganisationComplianceDeclarationEligibility>> ApplyCurrentObligationMetrics(
        IReadOnlyList<OrganisationComplianceDeclarationEligibility> rows,
        CancellationToken cancellationToken
    )
    {
        if (rows.Count == 0)
            return rows;

        var organisationIds = rows.Select(x => x.OrganisationId).Distinct().ToArray();
        var obligationYears = rows.Select(x => x.ObligationYear).Distinct().ToArray();
        var summaries = await dbContext
            .OrganisationObligationSummaries.Find(x =>
                organisationIds.Contains(x.OrganisationId) && obligationYears.Contains(x.ObligationYear)
            )
            .ToListAsync(cancellationToken);
        var summariesByKey = summaries.ToDictionary(x => (x.OrganisationId, x.ObligationYear));

        return rows.Select(row =>
            {
                if (!summariesByKey.TryGetValue((row.OrganisationId, row.ObligationYear), out var summary))
                    return row;

                return row with
                {
                    RecyclingObligationsMet = summary.RecyclingObligationsMet,
                    ObligationCoveragePercentage = summary.ObligationCoveragePercentage ?? 0,
                };
            })
            .ToArray();
    }

    private async Task CollectGarbage(
        OrganisationEligibilitySnapshot snapshot,
        DateTime utcNow,
        CancellationToken cancellationToken
    )
    {
        var generationsToKeep = snapshot.RetainedGenerations.Select(x => x.Generation).ToList();
        if (snapshot.ActiveGeneration is not null)
        {
            generationsToKeep.Add(snapshot.ActiveGeneration);
        }

        var deleteBefore = utcNow.Subtract(options.Value.GenerationRetentionPeriod);
        var filter = Builders<OrganisationComplianceDeclarationEligibility>.Filter.And(
            Builders<OrganisationComplianceDeclarationEligibility>.Filter.Nin(x => x.Generation, generationsToKeep),
            Builders<OrganisationComplianceDeclarationEligibility>.Filter.Lte(x => x.RefreshedAt, deleteBefore)
        );
        await dbContext.OrganisationComplianceDeclarationEligibilities.DeleteManyAsync(filter, cancellationToken);
    }

    private RetainedOrganisationEligibilityGeneration[] RetainedGenerationsAfterPromotion(
        OrganisationEligibilitySnapshot? activeSnapshot,
        DateTime utcNow
    )
    {
        if (activeSnapshot?.ActiveGeneration is null)
        {
            return [];
        }

        return
        [
            .. UnexpiredRetainedGenerations(activeSnapshot, utcNow)
                .Where(x => x.Generation != activeSnapshot.ActiveGeneration),
            new RetainedOrganisationEligibilityGeneration
            {
                Generation = activeSnapshot.ActiveGeneration,
                DeleteAfter = utcNow.Add(options.Value.GenerationRetentionPeriod),
            },
        ];
    }

    private static RetainedOrganisationEligibilityGeneration[] UnexpiredRetainedGenerations(
        OrganisationEligibilitySnapshot snapshot,
        DateTime utcNow
    ) => snapshot.RetainedGenerations.Where(x => x.DeleteAfter > utcNow).ToArray();

    private async Task PromoteActiveGeneration(
        IClientSessionHandle transactionSession,
        OrganisationEligibilitySnapshot? activeSnapshot,
        OrganisationEligibilitySnapshot replacement,
        CancellationToken cancellationToken
    )
    {
        if (activeSnapshot is null)
        {
            try
            {
                await dbContext.OrganisationEligibilitySnapshots.InsertOneAsync(
                    transactionSession,
                    replacement,
                    cancellationToken: cancellationToken
                );
            }
            catch (MongoCommandException exception) when (exception.Code == DuplicateKeyErrorCode)
            {
                throw new InvalidOperationException(ActiveGenerationChangedMessage, exception);
            }
            catch (MongoWriteException exception) when (exception.WriteError.Code == DuplicateKeyErrorCode)
            {
                throw new InvalidOperationException(ActiveGenerationChangedMessage, exception);
            }

            return;
        }

        var result = await dbContext.OrganisationEligibilitySnapshots.ReplaceOneAsync(
            transactionSession,
            ActiveGenerationAndMaterialisedStateVersionFilter(activeSnapshot),
            replacement,
            cancellationToken: cancellationToken
        );

        EnsureActiveGenerationUnchanged(result.MatchedCount);
    }

    private static FilterDefinition<OrganisationEligibilitySnapshot> ActiveGenerationFilter(string? activeGeneration) =>
        Builders<OrganisationEligibilitySnapshot>.Filter.And(
            Builders<OrganisationEligibilitySnapshot>.Filter.Eq(x => x.Id, OrganisationEligibilitySnapshot.SnapshotId),
            Builders<OrganisationEligibilitySnapshot>.Filter.Eq(x => x.ActiveGeneration, activeGeneration)
        );

    private static FilterDefinition<OrganisationEligibilitySnapshot> ActiveGenerationAndMaterialisedStateVersionFilter(
        OrganisationEligibilitySnapshot snapshot
    ) =>
        ActiveGenerationFilter(snapshot.ActiveGeneration)
        & Builders<OrganisationEligibilitySnapshot>.Filter.Eq(
            x => x.MaterialisedStateVersion,
            snapshot.MaterialisedStateVersion
        );

    private static void EnsureActiveGenerationUnchanged(long matchedCount)
    {
        if (matchedCount != 1)
        {
            throw new InvalidOperationException(ActiveGenerationChangedMessage);
        }
    }
}
