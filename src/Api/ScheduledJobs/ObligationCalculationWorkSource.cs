using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Services.Scheduling;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Api.ScheduledJobs;

public class ObligationCalculationWorkSource(IMongoDatabase database)
    : IScheduledJobWorkSource<ObligationCalculationInstruction>
{
    private readonly IMongoCollection<ObligationCalculationInstruction> _instructions =
        database.GetCollection<ObligationCalculationInstruction>(ObligationCalculationInstruction.CollectionName);

    public async Task<ScheduledJobWork<ObligationCalculationInstruction>?> GetNext(
        IReadOnlyCollection<string> excludedIds,
        CancellationToken cancellationToken
    )
    {
        var filter = ActiveFilter();
        if (excludedIds.Count > 0)
            filter &= Builders<ObligationCalculationInstruction>.Filter.Nin(
                x => x.Id,
                excludedIds.Select(ObjectId.Parse)
            );
        var instruction = await _instructions
            .Find(filter)
            .SortBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return instruction is null
            ? null
            : new(
                instruction.Id.ToString(),
                instruction,
                string.IsNullOrWhiteSpace(instruction.CorrelationId)
                    ? instruction.Id.ToString()
                    : instruction.CorrelationId
            );
    }

    public async Task<bool> TryStart(
        ScheduledJobWork<ObligationCalculationInstruction> work,
        CancellationToken cancellationToken
    )
    {
        var result = await _instructions.UpdateOneAsync(
            ActiveFilter() & Builders<ObligationCalculationInstruction>.Filter.Eq(x => x.Id, work.Payload.Id),
            Builders<ObligationCalculationInstruction>.Update.Set(
                x => x.Status,
                ObligationCalculationInstructionStatus.Running
            ),
            cancellationToken: cancellationToken
        );

        return result.MatchedCount == 1;
    }

    public async Task RecordFailure(
        ScheduledJobWork<ObligationCalculationInstruction> work,
        int maximumAttempts,
        CancellationToken cancellationToken
    )
    {
        PipelineDefinition<ObligationCalculationInstruction, ObligationCalculationInstruction> pipeline =
            new BsonDocument[]
            {
                new(
                    "$set",
                    new BsonDocument(
                        "failureCount",
                        new BsonDocument(
                            "$add",
                            new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$failureCount", 0 }), 1 }
                        )
                    )
                ),
                new(
                    "$set",
                    new BsonDocument(
                        "status",
                        new BsonDocument(
                            "$cond",
                            new BsonArray
                            {
                                new BsonDocument("$gte", new BsonArray { "$failureCount", maximumAttempts }),
                                nameof(ObligationCalculationInstructionStatus.Failed),
                                nameof(ObligationCalculationInstructionStatus.Running),
                            }
                        )
                    )
                ),
            };
        var result = await _instructions.UpdateOneAsync(
            x => x.Id == work.Payload.Id && x.Status == ObligationCalculationInstructionStatus.Running,
            Builders<ObligationCalculationInstruction>.Update.Pipeline(pipeline),
            cancellationToken: cancellationToken
        );
        if (result.MatchedCount != 1)
            throw new InvalidOperationException(
                "The instruction is no longer running and its failure cannot be recorded"
            );
    }

    public async Task Complete(
        ScheduledJobWork<ObligationCalculationInstruction> work,
        CancellationToken cancellationToken
    )
    {
        var result = await _instructions.UpdateOneAsync(
            x => x.Id == work.Payload.Id && x.Status == ObligationCalculationInstructionStatus.Running,
            Builders<ObligationCalculationInstruction>.Update.Set(
                x => x.Status,
                ObligationCalculationInstructionStatus.Completed
            ),
            cancellationToken: cancellationToken
        );
        if (result.MatchedCount != 1)
            throw new InvalidOperationException("The instruction is no longer running and cannot be completed");
    }

    private static FilterDefinition<ObligationCalculationInstruction> ActiveFilter() =>
        Builders<ObligationCalculationInstruction>.Filter.In(
            x => x.Status,
            [ObligationCalculationInstructionStatus.Pending, ObligationCalculationInstructionStatus.Running]
        );
}
