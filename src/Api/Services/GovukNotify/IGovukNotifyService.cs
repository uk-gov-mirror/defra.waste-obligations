namespace Defra.WasteObligations.Api.Services.GovukNotify;

public interface IGovukNotifyService
{
    Task SendComplianceDeclarationSubmittedEmail(
        GovukNotifyOptions.TemplateName template,
        IEnumerable<string> recipients,
        Dictionary<string, object> personalisation,
        string language
    );

    Task SendComplianceDeclarationCancelledEmail(
        GovukNotifyOptions.TemplateName template,
        IEnumerable<(string Email, Dictionary<string, object> Personalisation)> recipients,
        string language
    );
}
