using AutoFixture;
using AutoFixture.Dsl;
using Defra.WasteObligations.AuditEvents.Entities;

namespace Defra.WasteObligations.Testing.Fixtures.Entities;

public static class AuditEventFixture
{
    private static Fixture GetFixture() => new();

    public static IPostprocessComposer<AuditEvent> AddDefaults(this ICustomizationComposer<AuditEvent> composer)
    {
        return composer
            .With(x => x.Entity, "entity")
            .With(x => x.EntityId, "entity-1")
            .With(x => x.Operation, "insert")
            .With(x => x.EventType, "submission.created")
            .Without(x => x.DeletedReason)
            .With(x => x.OccurredAt, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .With(x => x.RecordedAt, new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc))
            .With(x => x.Actor, "user@example.com")
            .With(x => x.Version, 1)
            .With(x => x.SchemaVersion, "v1.0")
            .With(x => x.Dispatches, [])
            .Without(x => x.Before)
            .Without(x => x.After);
    }

    public static IPostprocessComposer<AuditEvent> Event()
    {
        return GetFixture().Build<AuditEvent>().AddDefaults();
    }

    public static IPostprocessComposer<AuditEvent> Default(string eventId = "event-1", long sequence = 1)
    {
        return Event().With(x => x.EventId, eventId).With(x => x.Sequence, sequence);
    }

    public static IPostprocessComposer<AuditEvent> ComplianceDeclaration(string eventId = "event-1", long sequence = 1)
    {
        return Default(eventId, sequence)
            .With(x => x.Entity, "compliance_declaration")
            .With(x => x.EntityId, $"entity-{sequence}")
            .With(x => x.SchemaVersion, Api.Data.Entities.ComplianceDeclaration.SchemaVersionValue);
    }

    public static IPostprocessComposer<AuditEvent> ObligationCalculation(string eventId = "event-1", long sequence = 1)
    {
        return Default(eventId, sequence)
            .With(x => x.Entity, "obligation_calculation")
            .With(x => x.EntityId, MongoDB.Bson.ObjectId.GenerateNewId().ToString())
            .With(x => x.EventType, "obligation_calculation.created")
            .With(x => x.Actor, "system:waste-obligations")
            .With(x => x.SchemaVersion, Api.Data.Entities.ObligationCalculation.SchemaVersionValue);
    }
}
