using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Data.Migrations;
using Defra.WasteObligations.AuditEvents;
using Defra.WasteObligations.AuditEvents.Logging;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Defra.WasteObligations.Api.Services.ObligationCalculations;

public class ObligationCalculationService(IDbContext dbContext, IAuditEventService auditEventService)
    : IObligationCalculationService
{
    private const string VersionField = "version";

    public async Task<ObligationCalculation> Save(
        ObligationCalculation calculation,
        CancellationToken cancellationToken
    )
    {
        return (await SaveWithOutcome(calculation, cancellationToken)).Calculation;
    }

    public async Task<CalculationSaveResult> SaveWithOutcome(
        ObligationCalculation calculation,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(calculation);
        var currentCollection = dbContext.ObligationCalculations.Database.GetCollection<BsonDocument>(
            dbContext.ObligationCalculations.CollectionNamespace.CollectionName,
            dbContext.ObligationCalculations.Settings
        );
        var identity =
            Builders<ObligationCalculation>.Filter.Eq(
                x => x.SubmitterOrganisationId,
                calculation.SubmitterOrganisationId
            ) & Builders<ObligationCalculation>.Filter.Eq(x => x.ObligationYear, calculation.ObligationYear);
        var renderedIdentity = identity.Render(
            new RenderArgs<ObligationCalculation>(
                BsonSerializer.LookupSerializer<ObligationCalculation>(),
                BsonSerializer.SerializerRegistry
            )
        );
        // Capture the payload and expected version once, outside the transaction retry boundary.
        var previous = await currentCollection
            .WithReadPreference(ReadPreference.Primary)
            .Find(renderedIdentity)
            .SingleOrDefaultAsync(cancellationToken);
        var current = DeserializeCurrent(previous);
        if (current is not null && !calculation.RequiresNewVersion(current))
            return new CalculationSaveResult(current, CalculationSaveOutcome.Unchanged);

        var replacement = new ObligationCalculation
        {
            Id = previous is null ? ObjectId.GenerateNewId() : previous["_id"].AsObjectId,
            Version = current is null ? 1 : checked(current.Version + 1),
            SubmitterOrganisationId = calculation.SubmitterOrganisationId,
            SubmitterType = calculation.SubmitterType,
            ObligationYear = calculation.ObligationYear,
            CalculatedOn = calculation.CalculatedOn,
            Producers = calculation.Producers,
        };
        try
        {
            var saved = await dbContext.ExecuteTransaction(
                async (session, token) =>
                {
                    if (previous is null)
                    {
                        await dbContext.ObligationCalculations.InsertOneAsync(
                            session,
                            replacement,
                            cancellationToken: token
                        );
                    }
                    else
                    {
                        await ReplaceCurrent(session, currentCollection, previous, replacement, token);
                    }

                    await auditEventService.RecordEvent(
                        session,
                        new AuditEventRequest(
                            "system:waste-obligations",
                            "obligation_calculation",
                            previous is null ? AuditEventOperation.Insert : AuditEventOperation.Update,
                            previous is null ? "obligation_calculation.created" : "obligation_calculation.updated",
                            null,
                            replacement.Id.ToString(),
                            replacement.Version,
                            previous,
                            replacement.ToBsonDocument(),
                            replacement.SchemaVersion,
                            replacement.CalculatedOn,
                            CorrelationContext.Current
                        ),
                        token
                    );

                    return replacement;
                },
                "Save obligation calculation",
                cancellationToken
            );
            return new CalculationSaveResult(
                saved,
                previous is null ? CalculationSaveOutcome.Created : CalculationSaveOutcome.Updated
            );
        }
        catch (MongoWriteException exception) when (previous is null && IsIdentityCollision(exception.WriteError))
        {
            throw new ConcurrencyException(
                "An obligation calculation already exists for this organisation and year. Reload and try again.",
                exception
            );
        }
    }

    private async Task ReplaceCurrent(
        IClientSessionHandle session,
        IMongoCollection<BsonDocument> currentCollection,
        BsonDocument previous,
        ObligationCalculation replacement,
        CancellationToken token
    )
    {
        var expected = new BsonDocument { { "_id", previous["_id"] }, { VersionField, previous[VersionField] } };
        var stillCurrent = await currentCollection.Find(session, expected).AnyAsync(token);
        if (!stillCurrent)
            throw new ConcurrencyException("The obligation calculation has changed. Reload and try again.");

        var history = dbContext.ObligationCalculationHistory;
        var rawHistory = history.Database.GetCollection<BsonDocument>(
            history.CollectionNamespace.CollectionName,
            history.Settings
        );
        await rawHistory.InsertOneAsync(
            session,
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "calculation", previous } },
            cancellationToken: token
        );
        var result = await currentCollection.ReplaceOneAsync(
            session,
            expected,
            replacement.ToBsonDocument(),
            cancellationToken: token
        );
        if (result.MatchedCount != 1)
            throw new ConcurrencyException("The obligation calculation has changed. Reload and try again.");
    }

    private static ObligationCalculation? DeserializeCurrent(BsonDocument? previous) =>
        previous is null ? null : BsonSerializer.Deserialize<ObligationCalculation>(previous);

    private static bool IsIdentityCollision(WriteError error) =>
        error.Category == ServerErrorCategory.DuplicateKey
        && (
            (
                error.Details?.GetValue("keyPattern", BsonNull.Value) is BsonDocument pattern
                && pattern.Equals(new BsonDocument { { "submitterOrganisationId", 1 }, { "obligationYear", 1 } })
            )
            || error.Message.Contains(
                $" index: {ObligationCalculationIndexes.IdentityIndexName} dup key:",
                StringComparison.Ordinal
            )
        );
}
