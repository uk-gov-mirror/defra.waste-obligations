namespace Defra.WasteObligations.AuditEvents.Analytics;

public static class ObligationCalculationEventContract
{
    public const string Entity = "obligation_calculation";
    public const string CreatedEventType = Entity + ".created";
    public const string UpdatedEventType = Entity + ".updated";
    public const string SchemaVersion = Entity + "_v1.0";
    public const string IdentityPrefix = "oblcalc_";
}
