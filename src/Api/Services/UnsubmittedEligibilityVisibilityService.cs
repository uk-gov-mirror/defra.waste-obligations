using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using MongoDB.Driver;

namespace Defra.WasteObligations.Api.Services;

public class UnsubmittedEligibilityVisibilityService(IDbContext dbContext) : IUnsubmittedEligibilityVisibilityService
{
    public async Task<IReadOnlyList<OrganisationComplianceDeclarationEligibility>> Apply(
        IReadOnlyList<OrganisationComplianceDeclarationEligibility> rows,
        DateTime utcNow,
        CancellationToken cancellationToken
    )
    {
        var excludedKeys = await ReadExcludedKeys(rows, cancellationToken);

        return rows.Select(row =>
                row with
                {
                    IsVisibleInUnsubmittedView = IsVisible(row, excludedKeys),
                    DeclarationStateUpdatedAt = utcNow,
                }
            )
            .ToArray();
    }

    public async Task Refresh(
        IClientSessionHandle transactionSession,
        IReadOnlyCollection<ComplianceDeclaration> declarations,
        DateTime utcNow,
        CancellationToken cancellationToken
    )
    {
        var activeGeneration = await dbContext
            .OrganisationEligibilitySnapshots.Find(
                transactionSession,
                x => x.Id == OrganisationEligibilitySnapshot.SnapshotId
            )
            .Project(x => x.ActiveGeneration)
            .SingleOrDefaultAsync(cancellationToken);
        if (activeGeneration is null)
        {
            await OrganisationEligibilitySnapshotState.IncrementMaterialisedStateVersion(
                dbContext,
                transactionSession,
                cancellationToken
            );

            return;
        }

        foreach (var key in declarations.Select(UnsubmittedEligibilityKey.From).Distinct())
        {
            var hasExcludingDeclaration = await dbContext
                .ComplianceDeclarations.Find(
                    transactionSession,
                    x =>
                        x.Organisation.Id == key.OrganisationId
                        && x.ObligationYear == key.ObligationYear
                        && x.Organisation.RegistrationType == key.RegistrationType
                        && (
                            x.Status == ComplianceDeclarationStatus.Submitted
                            || x.Status == ComplianceDeclarationStatus.Accepted
                        )
                )
                .AnyAsync(cancellationToken);
            var filter = Builders<OrganisationComplianceDeclarationEligibility>.Filter.And(
                Builders<OrganisationComplianceDeclarationEligibility>.Filter.Eq(x => x.Generation, activeGeneration),
                Builders<OrganisationComplianceDeclarationEligibility>.Filter.Eq(
                    x => x.OrganisationId,
                    key.OrganisationId
                ),
                Builders<OrganisationComplianceDeclarationEligibility>.Filter.Eq(
                    x => x.ObligationYear,
                    key.ObligationYear
                ),
                Builders<OrganisationComplianceDeclarationEligibility>.Filter.Eq(
                    x => x.RegistrationType,
                    key.RegistrationType
                )
            );
            var update = Builders<OrganisationComplianceDeclarationEligibility>
                .Update.Set(x => x.IsVisibleInUnsubmittedView, false)
                .Set(x => x.DeclarationStateUpdatedAt, utcNow);

            await dbContext.OrganisationComplianceDeclarationEligibilities.UpdateManyAsync(
                transactionSession,
                filter,
                update,
                cancellationToken: cancellationToken
            );

            if (hasExcludingDeclaration)
                continue;

            var visibleFilter =
                filter
                & Builders<OrganisationComplianceDeclarationEligibility>.Filter.Eq(
                    x => x.RegistrationStatus,
                    OrganisationRegistrationStatus.Registered
                )
                & Builders<OrganisationComplianceDeclarationEligibility>.Filter.Eq(
                    x => x.ReferenceNumberResolutionState,
                    OrganisationReferenceNumberResolutionState.Resolved
                )
                & Builders<OrganisationComplianceDeclarationEligibility>.Filter.Ne(x => x.ReferenceNumber, null);
            await dbContext.OrganisationComplianceDeclarationEligibilities.UpdateManyAsync(
                transactionSession,
                visibleFilter,
                Builders<OrganisationComplianceDeclarationEligibility>
                    .Update.Set(x => x.IsVisibleInUnsubmittedView, true)
                    .Set(x => x.DeclarationStateUpdatedAt, utcNow),
                cancellationToken: cancellationToken
            );
        }

        await OrganisationEligibilitySnapshotState.IncrementMaterialisedStateVersion(
            dbContext,
            transactionSession,
            cancellationToken
        );
    }

    private async Task<HashSet<UnsubmittedEligibilityKey>> ReadExcludedKeys(
        IReadOnlyList<OrganisationComplianceDeclarationEligibility> rows,
        CancellationToken cancellationToken
    )
    {
        if (rows.Count == 0)
            return [];

        var organisationIds = rows.Select(x => x.OrganisationId).Distinct().ToArray();
        var obligationYears = rows.Select(x => x.ObligationYear).Distinct().ToArray();
        var registrationTypes = rows.Select(x => x.RegistrationType).Distinct().ToArray();
        var declarations = await dbContext
            .ComplianceDeclarations.Find(
                Builders<ComplianceDeclaration>.Filter.And(
                    Builders<ComplianceDeclaration>.Filter.In(x => x.Organisation.Id, organisationIds),
                    Builders<ComplianceDeclaration>.Filter.In(x => x.ObligationYear, obligationYears),
                    Builders<ComplianceDeclaration>.Filter.In(x => x.Organisation.RegistrationType, registrationTypes),
                    Builders<ComplianceDeclaration>.Filter.In(
                        x => x.Status,
                        [ComplianceDeclarationStatus.Submitted, ComplianceDeclarationStatus.Accepted]
                    )
                )
            )
            .ToListAsync(cancellationToken);

        return declarations.Select(UnsubmittedEligibilityKey.From).ToHashSet();
    }

    private static bool IsVisible(
        OrganisationComplianceDeclarationEligibility row,
        HashSet<UnsubmittedEligibilityKey> excludedKeys
    ) =>
        row.RegistrationStatus == OrganisationRegistrationStatus.Registered
        && row.ReferenceNumberResolutionState == OrganisationReferenceNumberResolutionState.Resolved
        && !string.IsNullOrWhiteSpace(row.ReferenceNumber)
        && !excludedKeys.Contains(
            new UnsubmittedEligibilityKey(row.OrganisationId, row.ObligationYear, row.RegistrationType)
        );
}
