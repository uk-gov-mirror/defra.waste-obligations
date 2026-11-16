using Defra.WasteObligations.AuditEvents.Entities;

namespace Defra.WasteObligations.AuditEvents.Analytics;

public static class AnalyticsEventMappers
{
    public static AnalyticsEvent ToAnalyticsEvent(this AuditEvent auditEvent)
    {
        var analyticsEvent = new AnalyticsEvent
        {
            EventId = auditEvent.EventId,
            Sequence = auditEvent.Sequence,
            Entity = auditEvent.Entity,
            EntityId =
                auditEvent.Entity == "obligation_calculation"
                    ? $"oblcalc_{auditEvent.EntityId}"
                    : $"cdec_{auditEvent.EntityId}",
            Operation = AnalyticsEventVocabulary.ToOperation(auditEvent.Operation),
            EventType = auditEvent.EventType,
            DeletedReason = AnalyticsEventVocabulary.NormalizeDeletedReason(auditEvent.DeletedReason),
            PiiKeyRef = null,
            OccurredAt = auditEvent.OccurredAt,
            RecordedAt = auditEvent.RecordedAt,
            Actor = auditEvent.Actor,
            CorrelationId = auditEvent.TraceId,
            Version = auditEvent.Version,
            Before = auditEvent.Before,
            After = auditEvent.After,
            SchemaVersion = $"{auditEvent.Entity}_{auditEvent.SchemaVersion}",
        };

        AnalyticsEventVocabulary.Validate(analyticsEvent);

        return analyticsEvent;
    }
}
