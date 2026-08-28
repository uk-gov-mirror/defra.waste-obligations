using System.Text.RegularExpressions;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Services.OrganisationEligibility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using OrganisationComplianceDeclarationEligibilityEntity = Defra.WasteObligations.Api.Data.Entities.OrganisationComplianceDeclarationEligibility;

namespace Defra.WasteObligations.Api.Services;

public class UnsubmittedOrganisationsService(
    IDbContext dbContext,
    IOptions<OrganisationEligibilityOptions> options,
    TimeProvider timeProvider,
    ILogger<UnsubmittedOrganisationsService> logger
) : IUnsubmittedOrganisationsService
{
    public async Task<UnsubmittedOrganisationSearchResult> Search(
        int? obligationYear,
        IReadOnlyCollection<RegistrationType>? registrationTypes,
        string? search,
        IReadOnlyCollection<UnsubmittedOrganisationSort>? sort,
        int page,
        int pageSize,
        CancellationToken cancellationToken
    )
    {
        var snapshot = await dbContext
            .OrganisationEligibilitySnapshots.Find(x => x.Id == OrganisationEligibilitySnapshot.SnapshotId)
            .SingleOrDefaultAsync(cancellationToken);
        if (snapshot?.ActiveGeneration is not { } activeGeneration)
        {
            logger.LogError("Unsubmitted organisation query has no active organisation generation");

            return EmptyResult();
        }

        if (
            snapshot.LastVerifiedAt is null
            || timeProvider.GetUtcNow().UtcDateTime - snapshot.LastVerifiedAt.Value
                > options.Value.MaximumAllowedStaleness
        )
        {
            logger.LogError(
                "Unsubmitted organisation query is using an organisation generation last verified at {LastVerifiedAt}",
                snapshot.LastVerifiedAt
            );
        }

        // This materialised membership field already requires a Registered row, resolved reference,
        // and no Submitted or Accepted declaration for the organisation/year/registration type.
        var filters = new List<FilterDefinition<OrganisationComplianceDeclarationEligibilityEntity>>
        {
            Builders<OrganisationComplianceDeclarationEligibilityEntity>.Filter.Eq(x => x.Generation, activeGeneration),
            Builders<OrganisationComplianceDeclarationEligibilityEntity>.Filter.Eq(
                x => x.IsVisibleInUnsubmittedView,
                true
            ),
        };
        if (obligationYear.HasValue)
        {
            filters.Add(
                Builders<OrganisationComplianceDeclarationEligibilityEntity>.Filter.Eq(
                    x => x.ObligationYear,
                    obligationYear.Value
                )
            );
        }

        if (registrationTypes is { Count: > 0 })
        {
            filters.Add(
                Builders<OrganisationComplianceDeclarationEligibilityEntity>.Filter.In(
                    x => x.RegistrationType,
                    registrationTypes
                )
            );
        }

        var eligible = Builders<OrganisationComplianceDeclarationEligibilityEntity>.Filter.And(filters);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = new BsonRegularExpression(Regex.Escape(search.Trim()), "i");
            eligible &= Builders<OrganisationComplianceDeclarationEligibilityEntity>.Filter.Or(
                Builders<OrganisationComplianceDeclarationEligibilityEntity>.Filter.Regex(x => x.Name, pattern),
                Builders<OrganisationComplianceDeclarationEligibilityEntity>.Filter.Regex(
                    x => x.ReferenceNumber,
                    pattern
                )
            );
        }

        var rowsTask = dbContext
            .OrganisationComplianceDeclarationEligibilities.Find(eligible)
            .Sort(BuildSort(sort))
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .Project(x => new UnsubmittedOrganisationSearchRow
            {
                OrganisationId = x.OrganisationId,
                ObligationYear = x.ObligationYear,
                RegistrationType = x.RegistrationType,
                Name = x.Name,
                ReferenceNumber = x.ReferenceNumber!,
                RecyclingObligationsMet = x.RecyclingObligationsMet,
                ObligationCoveragePercentage = x.ObligationCoveragePercentage,
            })
            .ToListAsync(cancellationToken);
        var totalTask = dbContext.OrganisationComplianceDeclarationEligibilities.CountDocumentsAsync(
            eligible,
            cancellationToken: cancellationToken
        );
        await Task.WhenAll(rowsTask, totalTask);
        var rows = await rowsTask;
        var total = checked((int)await totalTask);

        return new UnsubmittedOrganisationSearchResult { Rows = rows, Total = total };
    }

    private static SortDefinition<OrganisationComplianceDeclarationEligibilityEntity> BuildSort(
        IReadOnlyCollection<UnsubmittedOrganisationSort>? sort
    )
    {
        var sortBuilder = Builders<OrganisationComplianceDeclarationEligibilityEntity>.Sort;
        if (sort is not { Count: > 0 })
            return sortBuilder.Combine(
                sortBuilder.Ascending(x => x.Name),
                sortBuilder.Ascending(x => x.OrganisationId)
            );

        var sortDefinitions = sort.Select(BuildSort).ToList();
        var tieBreakerDirection = sort.Last().Direction;
        if (!sort.Any(x => x.Field is UnsubmittedOrganisationSortField.Name))
        {
            sortDefinitions.Add(
                SortByDirection(
                    tieBreakerDirection,
                    sortBuilder.Ascending(x => x.Name),
                    sortBuilder.Descending(x => x.Name)
                )
            );
        }

        sortDefinitions.Add(
            SortByDirection(
                tieBreakerDirection,
                sortBuilder.Ascending(x => x.OrganisationId),
                sortBuilder.Descending(x => x.OrganisationId)
            )
        );

        return sortBuilder.Combine(sortDefinitions);
    }

    private static SortDefinition<OrganisationComplianceDeclarationEligibilityEntity> BuildSort(
        UnsubmittedOrganisationSort sort
    ) =>
        sort.Field switch
        {
            UnsubmittedOrganisationSortField.Name => SortByDirection(
                sort.Direction,
                Builders<OrganisationComplianceDeclarationEligibilityEntity>.Sort.Ascending(x => x.Name),
                Builders<OrganisationComplianceDeclarationEligibilityEntity>.Sort.Descending(x => x.Name)
            ),
            UnsubmittedOrganisationSortField.ReferenceNumber => sort.Direction
            is UnsubmittedOrganisationSortDirection.Ascending
                ? Builders<OrganisationComplianceDeclarationEligibilityEntity>.Sort.Ascending(x => x.ReferenceNumber)
                : Builders<OrganisationComplianceDeclarationEligibilityEntity>.Sort.Descending(x => x.ReferenceNumber),
            UnsubmittedOrganisationSortField.RecyclingObligationsMet => sort.Direction
            is UnsubmittedOrganisationSortDirection.Ascending
                ? Builders<OrganisationComplianceDeclarationEligibilityEntity>.Sort.Ascending(x =>
                    x.RecyclingObligationsMet
                )
                : Builders<OrganisationComplianceDeclarationEligibilityEntity>.Sort.Descending(x =>
                    x.RecyclingObligationsMet
                ),
            UnsubmittedOrganisationSortField.ObligationCoveragePercentage => sort.Direction
            is UnsubmittedOrganisationSortDirection.Ascending
                ? Builders<OrganisationComplianceDeclarationEligibilityEntity>.Sort.Ascending(x =>
                    x.ObligationCoveragePercentage
                )
                : Builders<OrganisationComplianceDeclarationEligibilityEntity>.Sort.Descending(x =>
                    x.ObligationCoveragePercentage
                ),
            _ => throw new ArgumentOutOfRangeException(nameof(sort)),
        };

    private static SortDefinition<OrganisationComplianceDeclarationEligibilityEntity> SortByDirection(
        UnsubmittedOrganisationSortDirection direction,
        SortDefinition<OrganisationComplianceDeclarationEligibilityEntity> ascending,
        SortDefinition<OrganisationComplianceDeclarationEligibilityEntity> descending
    ) => direction is UnsubmittedOrganisationSortDirection.Ascending ? ascending : descending;

    private static UnsubmittedOrganisationSearchResult EmptyResult() => new() { Rows = [], Total = 0 };
}
