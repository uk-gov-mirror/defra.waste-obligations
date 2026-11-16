using Defra.WasteObligations.AuditEvents.Data;
using Defra.WasteObligations.AuditEvents.Entities;
using MongoDB.Driver;

namespace Defra.WasteObligations.AuditEvents;

public class AuditEventService(
    IAuditEventDbContext dbContext,
    TimeProvider timeProvider,
    IEventIdGenerator eventIdGenerator
) : IAuditEventService
{
    private const string AuditEventCounterId = "audit_event";
    private const string InsertOperation = "insert";
    private const string UpdateOperation = "update";
    private const string DeleteOperation = "delete";

    public async Task RecordEvent(
        IClientSessionHandle session,
        AuditEventRequest auditEvent,
        CancellationToken cancellationToken
    )
    {
        var sequence = await AllocateSequence(session, cancellationToken);
        var utcNow = timeProvider.GetUtcNowWithoutMicroseconds();

        await dbContext.AuditEvents.InsertOneAsync(
            session,
            new AuditEvent
            {
                EventId = eventIdGenerator.Generate(),
                Sequence = sequence,
                Entity = auditEvent.Entity,
                EntityId = auditEvent.EntityId,
                Operation = ToValue(auditEvent.Operation),
                EventType = auditEvent.EventType,
                DeletedReason = auditEvent.DeletedReason,
                OccurredAt = auditEvent.OccurredAt,
                RecordedAt = utcNow < auditEvent.OccurredAt ? auditEvent.OccurredAt : utcNow,
                Actor = auditEvent.Actor,
                Version = auditEvent.Version,
                Before = auditEvent.Before,
                After = auditEvent.After,
                SchemaVersion = auditEvent.SchemaVersion,
                TraceId = auditEvent.TraceId,
            },
            cancellationToken: cancellationToken
        );
    }

    private async Task<long> AllocateSequence(IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var counter = await dbContext.AuditEventCounters.FindOneAndUpdateAsync(
            session,
            Builders<AuditEventCounter>.Filter.Eq(x => x.Id, AuditEventCounterId),
            Builders<AuditEventCounter>.Update.Inc(x => x.Sequence, 1),
            new FindOneAndUpdateOptions<AuditEventCounter> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
            cancellationToken
        );

        return counter.Sequence;
    }

    private static string ToValue(AuditEventOperation operation) =>
        operation switch
        {
            AuditEventOperation.Insert => InsertOperation,
            AuditEventOperation.Update => UpdateOperation,
            AuditEventOperation.Delete => DeleteOperation,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };
}
