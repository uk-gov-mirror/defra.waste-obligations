namespace Defra.WasteObligations.AuditEvents.Analytics;

public static class AnalyticsEventVocabulary
{
    public const string ElevatedSystemAllowedRemoval = "elevated_system_allowed_removal";

    private const string ComplianceDeclarationEntity = "compliance_declaration";
    private const string CreateOperation = "create";
    private const string UpdateOperation = "update";
    private const string DeleteOperation = "delete";
    private const string LegacyElevatedSystemAllowedRemoval = "elevated system allowed removal";

    private static readonly HashSet<string> s_eventTypes =
    [
        "submission.created",
        "submission.amended",
        "submission.removed",
    ];

    private static readonly HashSet<string> s_actorPrefixes = ["service", "user", "system", "integration"];

    private static readonly HashSet<string> s_schemaVersions =
    [
        "compliance_declaration_v1.0",
        "compliance_declaration_v1.1",
        "compliance_declaration_v1.2",
        "compliance_declaration_v1.3",
    ];

    public static string ToOperation(string operation) =>
        operation switch
        {
            "insert" => CreateOperation,
            UpdateOperation => UpdateOperation,
            DeleteOperation => DeleteOperation,
            _ => throw new InvalidOperationException($"Unregistered analytics operation '{operation}'."),
        };

    public static string? NormalizeDeletedReason(string? deletedReason) =>
        deletedReason switch
        {
            LegacyElevatedSystemAllowedRemoval => ElevatedSystemAllowedRemoval,
            _ => deletedReason,
        };

    public static void Validate(AnalyticsEvent analyticsEvent)
    {
        if (analyticsEvent.Entity is not (ComplianceDeclarationEntity or ObligationCalculationEventContract.Entity))
            throw new InvalidOperationException($"Unregistered analytics entity '{analyticsEvent.Entity}'.");

        if (analyticsEvent.Operation is not (CreateOperation or UpdateOperation or DeleteOperation))
            throw new InvalidOperationException($"Unregistered analytics operation '{analyticsEvent.Operation}'.");

        var isCalculation = analyticsEvent.Entity == ObligationCalculationEventContract.Entity;
        var registeredEventType = isCalculation
            ? analyticsEvent.Operation switch
            {
                CreateOperation => analyticsEvent.EventType == ObligationCalculationEventContract.CreatedEventType,
                UpdateOperation => analyticsEvent.EventType == ObligationCalculationEventContract.UpdatedEventType,
                _ => false,
            }
            : s_eventTypes.Contains(analyticsEvent.EventType);
        if (!registeredEventType)
            throw new InvalidOperationException($"Unregistered analytics event type '{analyticsEvent.EventType}'.");

        ValidateActor(analyticsEvent.Actor);

        var registeredSchemaVersion = isCalculation
            ? analyticsEvent.SchemaVersion == ObligationCalculationEventContract.SchemaVersion
            : s_schemaVersions.Contains(analyticsEvent.SchemaVersion);
        if (!registeredSchemaVersion)
            throw new InvalidOperationException(
                $"Unregistered analytics schema version '{analyticsEvent.SchemaVersion}'."
            );

        if (analyticsEvent.Operation == DeleteOperation)
        {
            if (analyticsEvent.DeletedReason != ElevatedSystemAllowedRemoval)
                throw new InvalidOperationException(
                    $"Unregistered analytics deletion reason '{analyticsEvent.DeletedReason}'."
                );

            return;
        }

        if (analyticsEvent.DeletedReason is not null)
            throw new InvalidOperationException("An analytics deletion reason is only valid for delete operations.");
    }

    private static void ValidateActor(string actor)
    {
        var separatorIndex = actor.IndexOf(':');
        if (
            separatorIndex <= 0
            || separatorIndex == actor.Length - 1
            || !s_actorPrefixes.Contains(actor[..separatorIndex])
        )
            throw new InvalidOperationException($"Unregistered analytics actor '{actor}'.");
    }
}
