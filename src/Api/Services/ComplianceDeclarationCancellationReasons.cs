using Defra.WasteObligations.Api.Services.GovukNotify;

namespace Defra.WasteObligations.Api.Services;

public static class ComplianceDeclarationCancellationReasons
{
    public const string NotSignedByCorrectPerson = "Not signed by correct person";
    public const string RecyclingObligationsChanged = "Recycling obligations changed";
    public const string ProducerCanMeetRecyclingObligations = "Producer can meet recycling obligations";
    public const string ComplianceSchemeCanMeetRecyclingObligations =
        "Compliance scheme can meet recycling obligations";
    public const string ProducerRequestedToCancel = "Producer requested to cancel";
    public const string ComplianceSchemeRequestedToCancel = "Compliance scheme requested to cancel";

    public static GovukNotifyOptions.TemplateName? TryGetTemplate(string reason) =>
        reason switch
        {
            NotSignedByCorrectPerson => GovukNotifyOptions
                .TemplateName
                .ComplianceDeclarationCancellationNotSignedByCorrectPerson,
            RecyclingObligationsChanged => GovukNotifyOptions
                .TemplateName
                .ComplianceDeclarationCancellationRecyclingObligationsChanged,
            ProducerCanMeetRecyclingObligations or ComplianceSchemeCanMeetRecyclingObligations => GovukNotifyOptions
                .TemplateName
                .ComplianceDeclarationCancellationCanMeetRecyclingObligations,
            ProducerRequestedToCancel or ComplianceSchemeRequestedToCancel => GovukNotifyOptions
                .TemplateName
                .ComplianceDeclarationCancellationProducerRequested,
            _ => null,
        };
}
