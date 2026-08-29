using AdaskoTheBeAsT.MongoDbMigrations.Abstractions;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Data.Migrations;
using Defra.WasteObligations.Api.Dtos;
using Defra.WasteObligations.AuditEvents.Data;
using Defra.WasteObligations.AuditEvents.Entities;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using AuditEventIndexesMigration = Defra.WasteObligations.Api.Data.Migrations.AuditEventIndexes;
using ComplianceDeclaration = Defra.WasteObligations.Api.Data.Entities.ComplianceDeclaration;
using ComplianceDeclarationStatus = Defra.WasteObligations.Api.Data.Entities.ComplianceDeclarationStatus;
using RegistrationType = Defra.WasteObligations.Api.Data.Entities.RegistrationType;

namespace Defra.WasteObligations.Api.IntegrationTests.Data;

public class MongoMigrationServiceTests : IntegrationTestBase
{
    private const string SchemaVersionV1_1 = "v1.1";
    private const string SchemaVersionV1_2 = "v1.2";
    private const string SchemaVersionV1_3 = "v1.3";
    private const string OrganisationIdObligationYearIndexName = "OrganisationId_ObligationYear";
    private const string SearchIndexName = "ObligationYear_Status_OrganisationRegistrationType";
    private const string BusinessCountrySearchIndexName =
        "BusinessCountry_ObligationYear_Status_OrganisationRegistrationType";
    private const string OrganisationNameIndexName = "OrganisationName";
    private const string SequenceIndexName = "Sequence";
    private const string EntityEntityIdVersionIndexName = "Entity_EntityId_Version";
    private const string DispatchAnalyticsIndexName = "Dispatch_analytics";
    private const string DispatchAnalyticsStatusNextAttemptAtSequenceIndexName =
        "Dispatch_analytics_Status_NextAttemptAt_Sequence";
    private const string OrganisationEligibilityNameIndexName =
        "Generation_IsVisibleInUnsubmittedView_Name_OrganisationId";
    private const string OrganisationEligibilityGenerationRowIndexName =
        "Generation_OrganisationId_ObligationYear_RegistrationType";
    private const string OrganisationEligibilityPercentageMetIndexName =
        "Generation_IsVisibleInUnsubmittedView_ObligationCoveragePercentage_Name_OrganisationId";
    private const string OrganisationEligibilityRecyclingObligationsIndexName =
        "Generation_IsVisibleInUnsubmittedView_RecyclingObligationsMet_Name_OrganisationId";
    private const string OrganisationEligibilityReferenceNumberIndexName =
        "Generation_IsVisibleInUnsubmittedView_ReferenceNumber_Name_OrganisationId";
    private const string OrganisationEligibilityOrganisationKeyIndexName =
        "OrganisationId_ObligationYear_RegistrationType";
    private const string OrganisationEligibilityHydrationIndexName =
        "Generation_ObligationYear_RegistrationStatus_ReferenceNumberResolutionState_OrganisationId";
    private const string OrganisationEligibilityExpiredGenerationIndexName = "RefreshedAt";
    private const string OrganisationObligationSummaryOrganisationYearIndexName = "OrganisationId_ObligationYear";
    private const string OrganisationObligationSummaryHydrationDueWorkIndexName =
        "ObligationYear_IsHydrationActive_Priority_NextRefreshAt";

    [Fact]
    public async Task Start_ShouldCreateIndex()
    {
        var database = GetMongoDatabase();
        var context = new MigrationContext(database, null!, TestContext.Current.CancellationToken);
        var subject = new MongoMigrationService(
            database,
            TimeProvider.System,
            Substitute.For<ILogger<MongoMigrationService>>()
        );
        await database.DropCollectionAsync("_migrations", TestContext.Current.CancellationToken);
        await database.DropCollectionAsync("_migrations_lease", TestContext.Current.CancellationToken);
        await new ComplianceDeclarationIndexes().DownAsync(context);
        await new AuditEventIndexesMigration().DownAsync(context);
        await new OrganisationObligationSummaryIndexes().DownAsync(context);

        await subject.StartAsync(TestContext.Current.CancellationToken);

        var complianceDeclarationIndexes = await (
            await ComplianceDeclarations.Indexes.ListAsync(TestContext.Current.CancellationToken)
        ).ToListAsync(TestContext.Current.CancellationToken);
        var auditEventIndexes = await (
            await AuditEvents.Indexes.ListAsync(TestContext.Current.CancellationToken)
        ).ToListAsync(TestContext.Current.CancellationToken);
        var sequenceKeys = new BsonDocument("sequence", 1);
        var entityKeys = new BsonDocument
        {
            ["entity"] = 1,
            ["entityId"] = 1,
            ["version"] = 1,
        };
        var dispatchKeys = new BsonDocument { ["dispatches.analytics"] = 1, ["sequence"] = 1 };
        var dispatchStatusNextAttemptAtSequenceKeys = new BsonDocument
        {
            ["dispatches.analytics.status"] = 1,
            ["dispatches.analytics.nextAttemptAt"] = 1,
            ["sequence"] = 1,
        };

        complianceDeclarationIndexes
            .Should()
            .Contain(x => IsIndex(x, OrganisationIdObligationYearIndexName, OrganisationReadIndexKeys()));
        complianceDeclarationIndexes
            .Should()
            .Contain(x => IsIndex(x, BusinessCountrySearchIndexName, BusinessCountrySearchIndexKeys()));
        auditEventIndexes.Should().Contain(x => IsIndex(x, SequenceIndexName, sequenceKeys, unique: true));
        auditEventIndexes.Should().Contain(x => IsIndex(x, EntityEntityIdVersionIndexName, entityKeys));
        auditEventIndexes.Should().Contain(x => IsIndex(x, DispatchAnalyticsIndexName, dispatchKeys));
        auditEventIndexes
            .Should()
            .Contain(x =>
                IsIndex(
                    x,
                    DispatchAnalyticsStatusNextAttemptAtSequenceIndexName,
                    dispatchStatusNextAttemptAtSequenceKeys
                )
            );
    }

    [Fact]
    public void AuditEventDbContext_ShouldUseSupportCollectionNameForCounters()
    {
        AuditEventCounters
            .CollectionNamespace.CollectionName.Should()
            .Be(AuditEventDbContext.AuditEventCounterCollectionName);
    }

    [Fact]
    public async Task OrganisationEligibilityIndexes_ShouldCreateAndDropIndexes()
    {
        var database = GetMongoDatabase();
        var context = new MigrationContext(database, null!, TestContext.Current.CancellationToken);
        var subject = new OrganisationEligibilityIndexes();
        var collection = database.GetCollection<OrganisationComplianceDeclarationEligibility>(
            nameof(OrganisationComplianceDeclarationEligibility)
        );
        await subject.DownAsync(context);

        await subject.UpAsync(context);

        var indexes = await (await collection.Indexes.ListAsync(TestContext.Current.CancellationToken)).ToListAsync(
            TestContext.Current.CancellationToken
        );
        indexes
            .Should()
            .Contain(x =>
                IsIndex(x, OrganisationEligibilityNameIndexName, OrganisationEligibilityNameIndexKeys(collection))
            );
        indexes
            .Should()
            .Contain(x =>
                IsIndex(
                    x,
                    OrganisationEligibilityGenerationRowIndexName,
                    OrganisationEligibilityGenerationRowIndexKeys(collection),
                    unique: true
                )
            );
        indexes
            .Should()
            .Contain(x =>
                IsIndex(
                    x,
                    OrganisationEligibilityReferenceNumberIndexName,
                    OrganisationEligibilityReferenceNumberIndexKeys(collection)
                )
            );
        indexes
            .Should()
            .Contain(x =>
                IsIndex(
                    x,
                    OrganisationEligibilityRecyclingObligationsIndexName,
                    OrganisationEligibilityRecyclingObligationsIndexKeys(collection)
                )
            );
        indexes
            .Should()
            .Contain(x =>
                IsIndex(
                    x,
                    OrganisationEligibilityPercentageMetIndexName,
                    OrganisationEligibilityPercentageMetIndexKeys(collection)
                )
            );

        await subject.DownAsync(context);
        await subject.DownAsync(context);
        indexes = await (await collection.Indexes.ListAsync(TestContext.Current.CancellationToken)).ToListAsync(
            TestContext.Current.CancellationToken
        );

        indexes.Should().NotContain(x => x.GetValue("name") == OrganisationEligibilityNameIndexName);
        indexes.Should().NotContain(x => x.GetValue("name") == OrganisationEligibilityGenerationRowIndexName);
        indexes.Should().NotContain(x => x.GetValue("name") == OrganisationEligibilityReferenceNumberIndexName);
        indexes.Should().NotContain(x => x.GetValue("name") == OrganisationEligibilityRecyclingObligationsIndexName);
        indexes.Should().NotContain(x => x.GetValue("name") == OrganisationEligibilityPercentageMetIndexName);

        await subject.UpAsync(context);
    }

    [Fact]
    public async Task OrganisationEligibilityApplicationQueryIndexes_ShouldCreateAndDropIndexes()
    {
        var database = GetMongoDatabase();
        var context = new MigrationContext(database, null!, TestContext.Current.CancellationToken);
        var subject = new OrganisationEligibilityApplicationQueryIndexes();
        await subject.DownAsync(context);

        await subject.UpAsync(context);

        var indexes = await (
            await OrganisationComplianceDeclarationEligibilities.Indexes.ListAsync(
                TestContext.Current.CancellationToken
            )
        ).ToListAsync(TestContext.Current.CancellationToken);
        indexes
            .Should()
            .Contain(x =>
                IsIndex(
                    x,
                    OrganisationEligibilityOrganisationKeyIndexName,
                    OrganisationEligibilityOrganisationKeyIndexKeys(OrganisationComplianceDeclarationEligibilities)
                )
            );
        indexes
            .Should()
            .Contain(x =>
                IsIndex(
                    x,
                    OrganisationEligibilityHydrationIndexName,
                    OrganisationEligibilityHydrationIndexKeys(OrganisationComplianceDeclarationEligibilities)
                )
            );
        indexes
            .Should()
            .Contain(x =>
                IsIndex(
                    x,
                    OrganisationEligibilityExpiredGenerationIndexName,
                    OrganisationEligibilityExpiredGenerationIndexKeys(OrganisationComplianceDeclarationEligibilities)
                )
            );

        await subject.DownAsync(context);
        await subject.DownAsync(context);
        indexes = await (
            await OrganisationComplianceDeclarationEligibilities.Indexes.ListAsync(
                TestContext.Current.CancellationToken
            )
        ).ToListAsync(TestContext.Current.CancellationToken);

        indexes.Should().NotContain(x => x.GetValue("name") == OrganisationEligibilityOrganisationKeyIndexName);
        indexes.Should().NotContain(x => x.GetValue("name") == OrganisationEligibilityHydrationIndexName);
        indexes.Should().NotContain(x => x.GetValue("name") == OrganisationEligibilityExpiredGenerationIndexName);

        await subject.UpAsync(context);
    }

    [Fact]
    public async Task OrganisationObligationSummaryIndexes_ShouldCreateAndDropIndexes()
    {
        var database = GetMongoDatabase();
        var context = new MigrationContext(database, null!, TestContext.Current.CancellationToken);
        var subject = new OrganisationObligationSummaryIndexes();
        await subject.DownAsync(context);

        await subject.UpAsync(context);

        var indexes = await (
            await OrganisationObligationSummaries.Indexes.ListAsync(TestContext.Current.CancellationToken)
        ).ToListAsync(TestContext.Current.CancellationToken);
        indexes
            .Should()
            .Contain(x =>
                IsIndex(
                    x,
                    OrganisationObligationSummaryOrganisationYearIndexName,
                    OrganisationObligationSummaryOrganisationYearIndexKeys(OrganisationObligationSummaries),
                    unique: true
                )
            );
        indexes
            .Should()
            .Contain(x =>
                IsIndex(
                    x,
                    OrganisationObligationSummaryHydrationDueWorkIndexName,
                    OrganisationObligationSummaryHydrationDueWorkIndexKeys(OrganisationObligationSummaries)
                )
            );

        await subject.DownAsync(context);
        await subject.DownAsync(context);
        indexes = await (
            await OrganisationObligationSummaries.Indexes.ListAsync(TestContext.Current.CancellationToken)
        ).ToListAsync(TestContext.Current.CancellationToken);

        indexes.Should().NotContain(x => x.GetValue("name") == OrganisationObligationSummaryOrganisationYearIndexName);
        indexes.Should().NotContain(x => x.GetValue("name") == OrganisationObligationSummaryHydrationDueWorkIndexName);

        await subject.UpAsync(context);
    }

    [Fact]
    public async Task ComplianceDeclarationIndexes_ShouldCreateReplaceAndDropIndexes()
    {
        var database = GetMongoDatabase();
        var context = new MigrationContext(database, null!, TestContext.Current.CancellationToken);
        var subject = new ComplianceDeclarationIndexes();
        await subject.DownAsync(context);
        await ComplianceDeclarations.Indexes.CreateOneAsync(
            new CreateIndexModel<ComplianceDeclaration>(
                Builders<ComplianceDeclaration>.IndexKeys.Ascending(x => x.Created),
                new CreateIndexOptions { Name = OrganisationIdObligationYearIndexName }
            ),
            cancellationToken: TestContext.Current.CancellationToken
        );

        await subject.UpAsync(context);

        var indexes = await ListComplianceDeclarationIndexes();
        indexes.Should().Contain(x => IsIndex(x, OrganisationIdObligationYearIndexName, OrganisationYearIndexKeys()));
        indexes.Should().Contain(x => x.GetValue("name") == SearchIndexName);
        indexes.Should().Contain(x => x.GetValue("name") == OrganisationNameIndexName);

        await subject.DownAsync(context);
        await subject.DownAsync(context);
        indexes = await ListComplianceDeclarationIndexes();

        indexes.Should().NotContain(x => x.GetValue("name") == OrganisationIdObligationYearIndexName);
        indexes.Should().NotContain(x => x.GetValue("name") == SearchIndexName);
        indexes.Should().NotContain(x => x.GetValue("name") == OrganisationNameIndexName);

        await subject.UpAsync(context);
    }

    [Fact]
    public async Task ComplianceDeclarationOrganisationReadIndex_ShouldCreateReplaceAndRestoreIndex()
    {
        var database = GetMongoDatabase();
        var context = new MigrationContext(database, null!, TestContext.Current.CancellationToken);
        var subject = new ComplianceDeclarationOrganisationReadIndex();
        await subject.DownAsync(context);

        var indexes = await ListComplianceDeclarationIndexes();
        indexes.Should().Contain(x => IsIndex(x, OrganisationIdObligationYearIndexName, OrganisationYearIndexKeys()));

        await subject.UpAsync(context);

        indexes = await ListComplianceDeclarationIndexes();
        indexes.Should().Contain(x => IsIndex(x, OrganisationIdObligationYearIndexName, OrganisationReadIndexKeys()));

        await subject.DownAsync(context);

        indexes = await ListComplianceDeclarationIndexes();
        indexes.Should().Contain(x => IsIndex(x, OrganisationIdObligationYearIndexName, OrganisationYearIndexKeys()));

        await subject.UpAsync(context);
    }

    [Fact]
    public async Task ComplianceDeclarationBusinessCountrySearchIndex_ShouldCreateReplaceAndDropIndex()
    {
        var database = GetMongoDatabase();
        var context = new MigrationContext(database, null!, TestContext.Current.CancellationToken);
        var subject = new ComplianceDeclarationBusinessCountrySearchIndex();
        await subject.DownAsync(context);
        await ComplianceDeclarations.Indexes.CreateOneAsync(
            new CreateIndexModel<ComplianceDeclaration>(
                Builders<ComplianceDeclaration>.IndexKeys.Ascending(x => x.Created),
                new CreateIndexOptions { Name = BusinessCountrySearchIndexName }
            ),
            cancellationToken: TestContext.Current.CancellationToken
        );

        await subject.UpAsync(context);

        var indexes = await ListComplianceDeclarationIndexes();
        indexes.Should().Contain(x => IsIndex(x, BusinessCountrySearchIndexName, BusinessCountrySearchIndexKeys()));

        await subject.DownAsync(context);
        await subject.DownAsync(context);
        indexes = await ListComplianceDeclarationIndexes();

        indexes.Should().NotContain(x => x.GetValue("name") == BusinessCountrySearchIndexName);

        await subject.UpAsync(context);
    }

    [Fact]
    public async Task ComplianceDeclarationRemoveOrganisationNameIndex_ShouldDropAndRestoreIndex()
    {
        var database = GetMongoDatabase();
        var context = new MigrationContext(database, null!, TestContext.Current.CancellationToken);
        var subject = new ComplianceDeclarationRemoveOrganisationNameIndex();
        await subject.DownAsync(context);

        await subject.UpAsync(context);
        await subject.UpAsync(context);

        var names = await ListComplianceDeclarationIndexNames();
        names.Should().NotContain(OrganisationNameIndexName);

        await subject.DownAsync(context);
        names = await ListComplianceDeclarationIndexNames();

        names.Should().Contain(OrganisationNameIndexName);

        await subject.UpAsync(context);
    }

    [Fact]
    public async Task AuditEventIndexes_ShouldCreateReplaceAndDropIndexes()
    {
        var database = GetMongoDatabase();
        var context = new MigrationContext(database, null!, TestContext.Current.CancellationToken);
        var subject = new AuditEventIndexesMigration();
        await subject.DownAsync(context);
        await AuditEvents.Indexes.CreateOneAsync(
            new CreateIndexModel<AuditEvent>(
                Builders<AuditEvent>.IndexKeys.Ascending(x => x.Actor),
                new CreateIndexOptions { Name = SequenceIndexName }
            ),
            cancellationToken: TestContext.Current.CancellationToken
        );

        await subject.UpAsync(context);

        var indexes = await ListAuditEventIndexes();
        var sequenceKeys = new BsonDocument("sequence", 1);
        var entityKeys = new BsonDocument
        {
            ["entity"] = 1,
            ["entityId"] = 1,
            ["version"] = 1,
        };
        var dispatchKeys = new BsonDocument { ["dispatches.analytics"] = 1, ["sequence"] = 1 };
        var dispatchStatusNextAttemptAtSequenceKeys = new BsonDocument
        {
            ["dispatches.analytics.status"] = 1,
            ["dispatches.analytics.nextAttemptAt"] = 1,
            ["sequence"] = 1,
        };
        indexes.Should().Contain(x => IsIndex(x, SequenceIndexName, sequenceKeys, unique: true));
        indexes.Should().Contain(x => IsIndex(x, EntityEntityIdVersionIndexName, entityKeys));
        indexes.Should().Contain(x => IsIndex(x, DispatchAnalyticsIndexName, dispatchKeys));
        indexes
            .Should()
            .Contain(x =>
                IsIndex(
                    x,
                    DispatchAnalyticsStatusNextAttemptAtSequenceIndexName,
                    dispatchStatusNextAttemptAtSequenceKeys
                )
            );

        await subject.DownAsync(context);
        await subject.DownAsync(context);
        indexes = await ListAuditEventIndexes();

        indexes.Should().NotContain(x => x.GetValue("name") == SequenceIndexName);
        indexes.Should().NotContain(x => x.GetValue("name") == EntityEntityIdVersionIndexName);
        indexes.Should().NotContain(x => x.GetValue("name") == DispatchAnalyticsIndexName);
        indexes.Should().NotContain(x => x.GetValue("name") == DispatchAnalyticsStatusNextAttemptAtSequenceIndexName);

        await subject.UpAsync(context);
    }

    [Fact]
    public async Task ComplianceDeclarationUserLocale_ShouldBumpSchemaVersionAndLeaveLegacyLocaleNull()
    {
        var database = GetMongoDatabase();
        var collection = database.GetCollection<BsonDocument>(nameof(ComplianceDeclaration));
        var context = new MigrationContext(database, null!, TestContext.Current.CancellationToken);
        var subject = new ComplianceDeclarationUserLocale();
        var legacyId = ObjectId.GenerateNewId();
        var existingLocaleId = ObjectId.GenerateNewId();
        var alreadyMigratedId = ObjectId.GenerateNewId();
        var timestamp = new DateTime(2026, 4, 26, 14, 0, 0, DateTimeKind.Utc);

        await collection.InsertManyAsync(
            [
                CreateLegacyComplianceDeclaration(
                    legacyId,
                    timestamp,
                    submittedUserLocale: null,
                    schemaVersion: "v1.0"
                ),
                CreateLegacyComplianceDeclaration(
                    existingLocaleId,
                    timestamp,
                    submittedUserLocale: UserLocale.Cy,
                    schemaVersion: "v1.0"
                ),
                CreateLegacyComplianceDeclaration(
                    alreadyMigratedId,
                    timestamp,
                    submittedUserLocale: UserLocale.En,
                    schemaVersion: SchemaVersionV1_1
                ),
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );

        await subject.UpAsync(context);

        var legacy = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", legacyId))
            .SingleAsync(TestContext.Current.CancellationToken);
        legacy["schemaVersion"].AsString.Should().Be(SchemaVersionV1_1);
        GetSubmittedAuditUser(legacy)["locale"].Should().Be(BsonNull.Value);

        var existingLocale = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", existingLocaleId))
            .SingleAsync(TestContext.Current.CancellationToken);
        existingLocale["schemaVersion"].AsString.Should().Be(SchemaVersionV1_1);
        GetSubmittedAuditUser(existingLocale)["locale"].AsString.Should().Be(UserLocale.Cy);

        var alreadyMigrated = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", alreadyMigratedId))
            .SingleAsync(TestContext.Current.CancellationToken);
        alreadyMigrated["schemaVersion"].AsString.Should().Be(SchemaVersionV1_1);
        GetSubmittedAuditUser(alreadyMigrated)["locale"].AsString.Should().Be(UserLocale.En);

        await subject.DownAsync(context);

        legacy = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", legacyId))
            .SingleAsync(TestContext.Current.CancellationToken);
        legacy["schemaVersion"].AsString.Should().Be("v1.0");
        GetSubmittedAuditUser(legacy).Contains("locale").Should().BeFalse();

        existingLocale = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", existingLocaleId))
            .SingleAsync(TestContext.Current.CancellationToken);
        existingLocale["schemaVersion"].AsString.Should().Be("v1.0");
        GetSubmittedAuditUser(existingLocale).Contains("locale").Should().BeFalse();
    }

    [Fact]
    public async Task ComplianceDeclarationObligationCoveragePercentage_ShouldBackfillAndBumpSchemaVersion()
    {
        var database = GetMongoDatabase();
        var collection = database.GetCollection<BsonDocument>(nameof(ComplianceDeclaration));
        var context = new MigrationContext(database, null!, TestContext.Current.CancellationToken);
        var subject = new ComplianceDeclarationObligationCoveragePercentage();
        var legacyId = ObjectId.GenerateNewId();
        var roundingLegacyId = ObjectId.GenerateNewId();
        var existingPercentageId = ObjectId.GenerateNewId();
        var alreadyMigratedId = ObjectId.GenerateNewId();
        var timestamp = new DateTime(2026, 4, 26, 14, 0, 0, DateTimeKind.Utc);
        const decimal existingPercentage = 75m;

        await collection.InsertManyAsync(
            [
                CreateLegacyComplianceDeclaration(
                    legacyId,
                    timestamp,
                    submittedUserLocale: null,
                    schemaVersion: SchemaVersionV1_1,
                    obligationCoveragePercentage: null
                ),
                CreateLegacyComplianceDeclaration(
                    roundingLegacyId,
                    timestamp,
                    submittedUserLocale: UserLocale.En,
                    schemaVersion: SchemaVersionV1_1,
                    obligationCoveragePercentage: null,
                    accepted: 1,
                    obligated: 3
                ),
                CreateLegacyComplianceDeclaration(
                    existingPercentageId,
                    timestamp,
                    submittedUserLocale: UserLocale.En,
                    schemaVersion: SchemaVersionV1_1,
                    obligationCoveragePercentage: existingPercentage
                ),
                CreateLegacyComplianceDeclaration(
                    alreadyMigratedId,
                    timestamp,
                    submittedUserLocale: UserLocale.En,
                    schemaVersion: SchemaVersionV1_2,
                    obligationCoveragePercentage: existingPercentage
                ),
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );

        await subject.UpAsync(context);

        var legacy = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", legacyId))
            .SingleAsync(TestContext.Current.CancellationToken);
        legacy["schemaVersion"].AsString.Should().Be(SchemaVersionV1_2);
        legacy[ObligationCoveragePercentageField].ToDecimal().Should().Be(40m);

        var roundingLegacy = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", roundingLegacyId))
            .SingleAsync(TestContext.Current.CancellationToken);
        roundingLegacy["schemaVersion"].AsString.Should().Be(SchemaVersionV1_2);
        roundingLegacy[ObligationCoveragePercentageField].ToDecimal().Should().Be(33m);

        var existingPercentageDocument = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", existingPercentageId))
            .SingleAsync(TestContext.Current.CancellationToken);
        existingPercentageDocument["schemaVersion"].AsString.Should().Be(SchemaVersionV1_2);
        existingPercentageDocument[ObligationCoveragePercentageField].ToDecimal().Should().Be(existingPercentage);

        var alreadyMigrated = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", alreadyMigratedId))
            .SingleAsync(TestContext.Current.CancellationToken);
        alreadyMigrated["schemaVersion"].AsString.Should().Be(SchemaVersionV1_2);
        alreadyMigrated[ObligationCoveragePercentageField].ToDecimal().Should().Be(existingPercentage);

        await subject.UpAsync(context);

        legacy[ObligationCoveragePercentageField].ToDecimal().Should().Be(40m);

        await subject.DownAsync(context);

        legacy = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", legacyId))
            .SingleAsync(TestContext.Current.CancellationToken);
        legacy["schemaVersion"].AsString.Should().Be(SchemaVersionV1_1);
        legacy.Contains(ObligationCoveragePercentageField).Should().BeFalse();

        existingPercentageDocument = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", existingPercentageId))
            .SingleAsync(TestContext.Current.CancellationToken);
        existingPercentageDocument["schemaVersion"].AsString.Should().Be(SchemaVersionV1_1);
        existingPercentageDocument.Contains(ObligationCoveragePercentageField).Should().BeFalse();
    }

    [Fact]
    public async Task ComplianceDeclarationObligationCoveragePercentagePrecision_ShouldRecalculateWithSumFormulaAndCap()
    {
        var database = GetMongoDatabase();
        var collection = database.GetCollection<BsonDocument>(nameof(ComplianceDeclaration));
        var context = new MigrationContext(database, null!, TestContext.Current.CancellationToken);
        var subject = new ComplianceDeclarationObligationCoveragePercentagePrecision();
        var recalculatedId = ObjectId.GenerateNewId();
        var cappedId = ObjectId.GenerateNewId();
        var scenario2Id = ObjectId.GenerateNewId();
        var multiMaterialId = ObjectId.GenerateNewId();
        var storedTwoDecimalPlacesId = ObjectId.GenerateNewId();
        var wholeNumberId = ObjectId.GenerateNewId();
        var zeroObligatedId = ObjectId.GenerateNewId();
        var timestamp = new DateTime(2026, 4, 26, 14, 0, 0, DateTimeKind.Utc);
        const decimal wholeNumberPercentage = 75m;

        await collection.InsertManyAsync(
            [
                CreateLegacyComplianceDeclaration(
                    recalculatedId,
                    timestamp,
                    submittedUserLocale: UserLocale.En,
                    schemaVersion: SchemaVersionV1_2,
                    obligationCoveragePercentage: null,
                    accepted: 1,
                    obligated: 3
                ),
                CreateLegacyComplianceDeclaration(
                    cappedId,
                    timestamp,
                    submittedUserLocale: UserLocale.En,
                    schemaVersion: SchemaVersionV1_2,
                    obligationCoveragePercentage: 124m,
                    accepted: 1150,
                    obligated: 925
                ),
                CreateLegacyComplianceDeclaration(
                    scenario2Id,
                    timestamp,
                    submittedUserLocale: UserLocale.En,
                    schemaVersion: SchemaVersionV1_2,
                    obligationCoveragePercentage: 92m,
                    accepted: 850,
                    obligated: 925
                ),
                CreateLegacyComplianceDeclarationWithObligations(
                    multiMaterialId,
                    timestamp,
                    submittedUserLocale: UserLocale.En,
                    schemaVersion: SchemaVersionV1_2,
                    obligationCoveragePercentage: 50m,
                    obligations:
                    [
                        CreateLegacyObligation("Plastic", accepted: 100, obligated: 50),
                        CreateLegacyObligation("Glass", accepted: 0, obligated: 50),
                    ]
                ),
                CreateLegacyComplianceDeclaration(
                    storedTwoDecimalPlacesId,
                    timestamp,
                    submittedUserLocale: UserLocale.En,
                    schemaVersion: SchemaVersionV1_2,
                    obligationCoveragePercentage: 33.33m,
                    accepted: 1,
                    obligated: 3
                ),
                CreateLegacyComplianceDeclaration(
                    wholeNumberId,
                    timestamp,
                    submittedUserLocale: UserLocale.En,
                    schemaVersion: SchemaVersionV1_2,
                    obligationCoveragePercentage: wholeNumberPercentage,
                    accepted: 3,
                    obligated: 4
                ),
                CreateLegacyComplianceDeclaration(
                    zeroObligatedId,
                    timestamp,
                    submittedUserLocale: UserLocale.En,
                    schemaVersion: SchemaVersionV1_2,
                    obligationCoveragePercentage: 10m,
                    accepted: 0,
                    obligated: 0
                ),
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );

        await subject.UpAsync(context);

        var recalculated = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", recalculatedId))
            .SingleAsync(TestContext.Current.CancellationToken);
        recalculated["schemaVersion"].AsString.Should().Be(SchemaVersionV1_2);
        recalculated[ObligationCoveragePercentageField].ToDecimal().Should().Be(33m);

        var capped = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", cappedId))
            .SingleAsync(TestContext.Current.CancellationToken);
        capped[ObligationCoveragePercentageField].ToDecimal().Should().Be(100m);

        var scenario2 = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", scenario2Id))
            .SingleAsync(TestContext.Current.CancellationToken);
        scenario2[ObligationCoveragePercentageField].ToDecimal().Should().Be(92m);

        var multiMaterial = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", multiMaterialId))
            .SingleAsync(TestContext.Current.CancellationToken);
        multiMaterial[ObligationCoveragePercentageField].ToDecimal().Should().Be(100m);

        var storedTwoDecimalPlaces = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", storedTwoDecimalPlacesId))
            .SingleAsync(TestContext.Current.CancellationToken);
        storedTwoDecimalPlaces[ObligationCoveragePercentageField].ToDecimal().Should().Be(33m);

        var wholeNumber = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", wholeNumberId))
            .SingleAsync(TestContext.Current.CancellationToken);
        wholeNumber[ObligationCoveragePercentageField].ToDecimal().Should().Be(wholeNumberPercentage);

        var zeroObligated = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", zeroObligatedId))
            .SingleAsync(TestContext.Current.CancellationToken);
        zeroObligated[ObligationCoveragePercentageField].ToDecimal().Should().Be(0m);

        await subject.UpAsync(context);

        recalculated[ObligationCoveragePercentageField].ToDecimal().Should().Be(33m);
        capped[ObligationCoveragePercentageField].ToDecimal().Should().Be(100m);

        await subject.DownAsync(context);

        recalculated = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", recalculatedId))
            .SingleAsync(TestContext.Current.CancellationToken);
        recalculated[ObligationCoveragePercentageField].ToDecimal().Should().Be(33.3m);

        multiMaterial = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", multiMaterialId))
            .SingleAsync(TestContext.Current.CancellationToken);
        multiMaterial[ObligationCoveragePercentageField].ToDecimal().Should().Be(50m);

        storedTwoDecimalPlaces = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", storedTwoDecimalPlacesId))
            .SingleAsync(TestContext.Current.CancellationToken);
        storedTwoDecimalPlaces[ObligationCoveragePercentageField].ToDecimal().Should().Be(33.3m);

        zeroObligated = await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", zeroObligatedId))
            .SingleAsync(TestContext.Current.CancellationToken);
        zeroObligated[ObligationCoveragePercentageField].ToDecimal().Should().Be(0m);
    }

    [Fact]
    public async Task ComplianceDeclarationBusinessCountry_ShouldBumpSchemaVersionAndPreserveExistingCountry()
    {
        var database = GetMongoDatabase();
        var collection = database.GetCollection<BsonDocument>(nameof(ComplianceDeclaration));
        var context = new MigrationContext(database, null!, TestContext.Current.CancellationToken);
        var subject = new ComplianceDeclarationBusinessCountry();
        var legacyId = ObjectId.GenerateNewId();
        var existingCountryId = ObjectId.GenerateNewId();
        var alreadyMigratedId = ObjectId.GenerateNewId();
        var missingSchemaVersionId = ObjectId.GenerateNewId();
        var timestamp = new DateTime(2026, 4, 26, 14, 0, 0, DateTimeKind.Utc);
        var existingCountry = CreateLegacyComplianceDeclaration(
            existingCountryId,
            timestamp,
            UserLocale.En,
            SchemaVersionV1_2
        );
        existingCountry["organisation"].AsBsonDocument["businessCountry"] = "GB-WLS";
        var alreadyMigrated = CreateLegacyComplianceDeclaration(
            alreadyMigratedId,
            timestamp,
            UserLocale.En,
            SchemaVersionV1_3
        );
        alreadyMigrated["organisation"].AsBsonDocument["businessCountry"] = "GB-SCT";
        var missingSchemaVersion = CreateLegacyComplianceDeclaration(
            missingSchemaVersionId,
            timestamp,
            UserLocale.En,
            SchemaVersionV1_2
        );
        missingSchemaVersion.Remove("schemaVersion");

        await collection.InsertManyAsync(
            [
                CreateLegacyComplianceDeclaration(legacyId, timestamp, UserLocale.En, SchemaVersionV1_2),
                existingCountry,
                alreadyMigrated,
                missingSchemaVersion,
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );

        await subject.UpAsync(context);
        await subject.UpAsync(context);

        var legacy = await FindComplianceDeclaration(collection, legacyId);
        legacy["schemaVersion"].AsString.Should().Be(SchemaVersionV1_3);
        legacy["organisation"].AsBsonDocument.Contains("businessCountry").Should().BeFalse();

        var existingCountryDocument = await FindComplianceDeclaration(collection, existingCountryId);
        existingCountryDocument["schemaVersion"].AsString.Should().Be(SchemaVersionV1_3);
        existingCountryDocument["organisation"].AsBsonDocument["businessCountry"].AsString.Should().Be("GB-WLS");

        var alreadyMigratedDocument = await FindComplianceDeclaration(collection, alreadyMigratedId);
        alreadyMigratedDocument["schemaVersion"].AsString.Should().Be(SchemaVersionV1_3);
        alreadyMigratedDocument["organisation"].AsBsonDocument["businessCountry"].AsString.Should().Be("GB-SCT");

        var legacyWithoutSchemaVersion = await FindComplianceDeclaration(collection, missingSchemaVersionId);
        legacyWithoutSchemaVersion.Contains("schemaVersion").Should().BeFalse();

        await subject.DownAsync(context);

        legacy = await FindComplianceDeclaration(collection, legacyId);
        legacy["schemaVersion"].AsString.Should().Be(SchemaVersionV1_2);

        existingCountryDocument = await FindComplianceDeclaration(collection, existingCountryId);
        existingCountryDocument["schemaVersion"].AsString.Should().Be(SchemaVersionV1_2);
        existingCountryDocument["organisation"].AsBsonDocument.Contains("businessCountry").Should().BeFalse();

        alreadyMigratedDocument = await FindComplianceDeclaration(collection, alreadyMigratedId);
        alreadyMigratedDocument["schemaVersion"].AsString.Should().Be(SchemaVersionV1_2);
        alreadyMigratedDocument["organisation"].AsBsonDocument.Contains("businessCountry").Should().BeFalse();
    }

    private const string ObligationCoveragePercentageField = "obligationCoveragePercentage";

    private static async Task<BsonDocument> FindComplianceDeclaration(
        IMongoCollection<BsonDocument> collection,
        ObjectId id
    ) =>
        await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", id))
            .SingleAsync(TestContext.Current.CancellationToken);

    private static BsonDocument CreateLegacyComplianceDeclaration(
        ObjectId id,
        DateTime timestamp,
        string? submittedUserLocale,
        string schemaVersion,
        decimal? obligationCoveragePercentage = null,
        int accepted = 2,
        int obligated = 5
    )
    {
        var submittedUser = new BsonDocument
        {
            ["_id"] = "e72be574-8b5b-4836-af47-dd7e0c0d1d87",
            ["email"] = "submitter@email.com",
            ["name"] = "Submitter Name",
        };

        if (submittedUserLocale is not null)
        {
            submittedUser["locale"] = submittedUserLocale;
        }

        var document = new BsonDocument
        {
            ["_id"] = id,
            ["schemaVersion"] = schemaVersion,
            ["version"] = 1,
            ["created"] = timestamp,
            ["updated"] = timestamp,
            ["status"] = nameof(ComplianceDeclarationStatus.Submitted),
            ["obligationYear"] = 2026,
            ["obligationStatus"] = "NotMet",
            ["submitterName"] = "Submitter Name",
            ["isRegulation43Compliant"] = true,
            ["organisation"] = new BsonDocument
            {
                ["_id"] = Guid.NewGuid().ToString(),
                ["registrationType"] = nameof(RegistrationType.DirectProducer),
                ["name"] = "Org Name",
                ["complianceSchemeName"] = BsonNull.Value,
                ["schemeOperatorName"] = BsonNull.Value,
                ["referenceNumber"] = "123456",
                ["regulator"] = "Regulator",
                ["regulatorEmail"] = "regulator@email.com",
            },
            ["obligations"] = new BsonArray
            {
                new BsonDocument
                {
                    ["material"] = "Plastic",
                    ["recyclingTarget"] = 0.75m,
                    ["status"] = "NoDataYet",
                    ["tonnages"] = new BsonDocument
                    {
                        ["material"] = 100,
                        ["awaitingAcceptance"] = 10,
                        ["accepted"] = accepted,
                        ["outstanding"] = 20,
                        ["obligated"] = obligated,
                    },
                },
            },
            ["audit"] = new BsonArray
            {
                new BsonDocument
                {
                    ["action"] = nameof(ComplianceDeclarationStatus.Submitted),
                    ["timestamp"] = timestamp,
                    ["user"] = submittedUser,
                },
            },
        };

        if (obligationCoveragePercentage is not null)
        {
            document[ObligationCoveragePercentageField] = new BsonDecimal128(obligationCoveragePercentage.Value);
        }

        return document;
    }

    private static BsonDocument CreateLegacyComplianceDeclarationWithObligations(
        ObjectId id,
        DateTime timestamp,
        string? submittedUserLocale,
        string schemaVersion,
        decimal? obligationCoveragePercentage,
        BsonArray obligations
    )
    {
        var document = CreateLegacyComplianceDeclaration(
            id,
            timestamp,
            submittedUserLocale,
            schemaVersion,
            obligationCoveragePercentage
        );
        document["obligations"] = obligations;

        return document;
    }

    private static BsonDocument CreateLegacyObligation(string material, int accepted, int obligated) =>
        new()
        {
            ["material"] = material,
            ["recyclingTarget"] = 0.75m,
            ["status"] = "NoDataYet",
            ["tonnages"] = new BsonDocument
            {
                ["material"] = 100,
                ["awaitingAcceptance"] = 10,
                ["accepted"] = accepted,
                ["outstanding"] = 20,
                ["obligated"] = obligated,
            },
        };

    private static BsonDocument GetSubmittedAuditUser(BsonDocument document) =>
        document["audit"]
            .AsBsonArray.Single(x =>
                x.AsBsonDocument["action"].AsString == nameof(ComplianceDeclarationStatus.Submitted)
            )["user"]
            .AsBsonDocument;

    private static bool IsIndex(BsonDocument index, string name, BsonDocument keys, bool unique = false) =>
        index.GetValue("name") == name
        && index.GetValue("key").AsBsonDocument == keys
        && (!unique || index.GetValue("unique", false).AsBoolean);

    private BsonDocument OrganisationReadIndexKeys() =>
        RenderIndexKeys(
            Builders<ComplianceDeclaration>
                .IndexKeys.Ascending(x => x.Organisation.Id)
                .Ascending(x => x.ObligationYear)
                .Descending(x => x.Updated)
                .Ascending(x => x.Id)
        );

    private BsonDocument OrganisationYearIndexKeys() =>
        RenderIndexKeys(
            Builders<ComplianceDeclaration>.IndexKeys.Ascending(x => x.Organisation.Id).Ascending(x => x.ObligationYear)
        );

    private BsonDocument BusinessCountrySearchIndexKeys() =>
        RenderIndexKeys(
            Builders<ComplianceDeclaration>
                .IndexKeys.Ascending(x => x.Organisation.BusinessCountry)
                .Ascending(x => x.ObligationYear)
                .Ascending(x => x.Status)
                .Ascending(x => x.Organisation.RegistrationType)
        );

    private BsonDocument RenderIndexKeys(IndexKeysDefinition<ComplianceDeclaration> keys) =>
        keys.Render(
            new RenderArgs<ComplianceDeclaration>(
                ComplianceDeclarations.DocumentSerializer,
                ComplianceDeclarations.Settings.SerializerRegistry
            )
        );

    private static BsonDocument OrganisationEligibilityNameIndexKeys(
        IMongoCollection<OrganisationComplianceDeclarationEligibility> collection
    ) =>
        RenderIndexKeys(
            collection,
            Builders<OrganisationComplianceDeclarationEligibility>
                .IndexKeys.Ascending(x => x.Generation)
                .Ascending(x => x.IsVisibleInUnsubmittedView)
                .Ascending(x => x.Name)
                .Ascending(x => x.OrganisationId)
        );

    private static BsonDocument OrganisationEligibilityGenerationRowIndexKeys(
        IMongoCollection<OrganisationComplianceDeclarationEligibility> collection
    ) =>
        RenderIndexKeys(
            collection,
            Builders<OrganisationComplianceDeclarationEligibility>
                .IndexKeys.Ascending(x => x.Generation)
                .Ascending(x => x.OrganisationId)
                .Ascending(x => x.ObligationYear)
                .Ascending(x => x.RegistrationType)
        );

    private static BsonDocument OrganisationEligibilityReferenceNumberIndexKeys(
        IMongoCollection<OrganisationComplianceDeclarationEligibility> collection
    ) =>
        RenderIndexKeys(
            collection,
            Builders<OrganisationComplianceDeclarationEligibility>
                .IndexKeys.Ascending(x => x.Generation)
                .Ascending(x => x.IsVisibleInUnsubmittedView)
                .Ascending(x => x.ReferenceNumber)
                .Ascending(x => x.Name)
                .Ascending(x => x.OrganisationId)
        );

    private static BsonDocument OrganisationEligibilityRecyclingObligationsIndexKeys(
        IMongoCollection<OrganisationComplianceDeclarationEligibility> collection
    ) =>
        RenderIndexKeys(
            collection,
            Builders<OrganisationComplianceDeclarationEligibility>
                .IndexKeys.Ascending(x => x.Generation)
                .Ascending(x => x.IsVisibleInUnsubmittedView)
                .Ascending(x => x.RecyclingObligationsMet)
                .Ascending(x => x.Name)
                .Ascending(x => x.OrganisationId)
        );

    private static BsonDocument OrganisationEligibilityPercentageMetIndexKeys(
        IMongoCollection<OrganisationComplianceDeclarationEligibility> collection
    ) =>
        RenderIndexKeys(
            collection,
            Builders<OrganisationComplianceDeclarationEligibility>
                .IndexKeys.Ascending(x => x.Generation)
                .Ascending(x => x.IsVisibleInUnsubmittedView)
                .Ascending(x => x.ObligationCoveragePercentage)
                .Ascending(x => x.Name)
                .Ascending(x => x.OrganisationId)
        );

    private static BsonDocument OrganisationEligibilityOrganisationKeyIndexKeys(
        IMongoCollection<OrganisationComplianceDeclarationEligibility> collection
    ) =>
        RenderIndexKeys(
            collection,
            Builders<OrganisationComplianceDeclarationEligibility>
                .IndexKeys.Ascending(x => x.OrganisationId)
                .Ascending(x => x.ObligationYear)
                .Ascending(x => x.RegistrationType)
        );

    private static BsonDocument OrganisationEligibilityHydrationIndexKeys(
        IMongoCollection<OrganisationComplianceDeclarationEligibility> collection
    ) =>
        RenderIndexKeys(
            collection,
            Builders<OrganisationComplianceDeclarationEligibility>
                .IndexKeys.Ascending(x => x.Generation)
                .Ascending(x => x.ObligationYear)
                .Ascending(x => x.RegistrationStatus)
                .Ascending(x => x.ReferenceNumberResolutionState)
                .Ascending(x => x.OrganisationId)
        );

    private static BsonDocument OrganisationEligibilityExpiredGenerationIndexKeys(
        IMongoCollection<OrganisationComplianceDeclarationEligibility> collection
    ) =>
        RenderIndexKeys(
            collection,
            Builders<OrganisationComplianceDeclarationEligibility>.IndexKeys.Ascending(x => x.RefreshedAt)
        );

    private static BsonDocument OrganisationObligationSummaryOrganisationYearIndexKeys(
        IMongoCollection<OrganisationObligationSummary> collection
    ) =>
        RenderIndexKeys(
            collection,
            Builders<OrganisationObligationSummary>
                .IndexKeys.Ascending(x => x.OrganisationId)
                .Ascending(x => x.ObligationYear)
        );

    private static BsonDocument OrganisationObligationSummaryHydrationDueWorkIndexKeys(
        IMongoCollection<OrganisationObligationSummary> collection
    ) =>
        RenderIndexKeys(
            collection,
            Builders<OrganisationObligationSummary>
                .IndexKeys.Ascending(x => x.ObligationYear)
                .Ascending(x => x.IsHydrationActive)
                .Ascending(x => x.Priority)
                .Ascending(x => x.NextRefreshAt)
        );

    private static BsonDocument RenderIndexKeys(
        IMongoCollection<OrganisationComplianceDeclarationEligibility> collection,
        IndexKeysDefinition<OrganisationComplianceDeclarationEligibility> keys
    ) =>
        keys.Render(
            new RenderArgs<OrganisationComplianceDeclarationEligibility>(
                collection.DocumentSerializer,
                collection.Settings.SerializerRegistry
            )
        );

    private static BsonDocument RenderIndexKeys(
        IMongoCollection<OrganisationObligationSummary> collection,
        IndexKeysDefinition<OrganisationObligationSummary> keys
    ) =>
        keys.Render(
            new RenderArgs<OrganisationObligationSummary>(
                collection.DocumentSerializer,
                collection.Settings.SerializerRegistry
            )
        );

    private async Task<List<string>> ListComplianceDeclarationIndexNames() =>
        [.. (await ListComplianceDeclarationIndexes()).Select(x => x.GetValue("name").AsString)];

    private async Task<List<BsonDocument>> ListComplianceDeclarationIndexes() =>
        await (await ComplianceDeclarations.Indexes.ListAsync(TestContext.Current.CancellationToken)).ToListAsync(
            TestContext.Current.CancellationToken
        );

    private async Task<List<BsonDocument>> ListAuditEventIndexes() =>
        await (await AuditEvents.Indexes.ListAsync(TestContext.Current.CancellationToken)).ToListAsync(
            TestContext.Current.CancellationToken
        );
}
