using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using Defra.WasteObligations.Api.Data.Entities;
using MongoDB.Driver;
using MigrationVersion = AdaskoTheBeAsT.MongoDbMigrations.Abstractions.Version;

namespace Defra.WasteObligations.Api.Data.Migrations;

[MigrationCollection(nameof(OrganisationComplianceDeclarationEligibility), MigrationDirection.Both)]
public class OrganisationEligibilityApplicationQueryIndexes : MongoMigration
{
    private const string HydrationEligibilityIndexName =
        "Generation_ObligationYear_RegistrationStatus_ReferenceNumberResolutionState_OrganisationId";
    private const string ExpiredGenerationIndexName = "RefreshedAt";

    public override MigrationVersion Version => new(1, 0, 11);

    public override string Name => "012 - Organisation eligibility application query indexes";

    public override async Task UpAsync(MigrationContext context)
    {
        await CreateIndex(
            context,
            HydrationEligibilityIndexName,
            Builders<OrganisationComplianceDeclarationEligibility>
                .IndexKeys.Ascending(x => x.Generation)
                .Ascending(x => x.ObligationYear)
                .Ascending(x => x.RegistrationStatus)
                .Ascending(x => x.ReferenceNumberResolutionState)
                .Ascending(x => x.OrganisationId)
        );
        await CreateIndex(
            context,
            ExpiredGenerationIndexName,
            Builders<OrganisationComplianceDeclarationEligibility>.IndexKeys.Ascending(x => x.RefreshedAt)
        );
    }

    public override async Task DownAsync(MigrationContext context)
    {
        await DropIndex<OrganisationComplianceDeclarationEligibility>(context, HydrationEligibilityIndexName);
        await DropIndex<OrganisationComplianceDeclarationEligibility>(context, ExpiredGenerationIndexName);
    }
}
