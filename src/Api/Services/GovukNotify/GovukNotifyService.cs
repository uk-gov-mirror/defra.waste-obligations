using Microsoft.Extensions.Options;
using Notify.Interfaces;

namespace Defra.WasteObligations.Api.Services.GovukNotify;

public class GovukNotifyService(
    HttpClient httpClient,
    IOptions<GovukNotifyOptions> options,
    Func<HttpClient, GovukNotifyOptions, IAsyncNotificationClient> notificationClientFactory
) : IGovukNotifyService
{
    public Task SendComplianceDeclarationSubmittedEmail(
        GovukNotifyOptions.TemplateName template,
        IEnumerable<string> recipients,
        Dictionary<string, object> personalisation,
        string language
    ) => SendComplianceDeclarationEmail(template, recipients, personalisation, language);

    public async Task SendComplianceDeclarationCancelledEmail(
        GovukNotifyOptions.TemplateName template,
        IEnumerable<(string Email, Dictionary<string, object> Personalisation)> recipients,
        string language
    )
    {
        var recipientList = recipients.ToArray();
        if (recipientList.Length == 0)
            return;

        var client = notificationClientFactory(httpClient, options.Value);
        var templateId = options.Value.Templates[template].GetTemplateId(language);

        await Task.WhenAll(recipientList.Select(x => client.SendEmailAsync(x.Email, templateId, x.Personalisation)));
    }

    private async Task SendComplianceDeclarationEmail(
        GovukNotifyOptions.TemplateName template,
        IEnumerable<string> recipients,
        Dictionary<string, object> personalisation,
        string language
    )
    {
        var client = notificationClientFactory(httpClient, options.Value);
        var templateId = options.Value.Templates[template].GetTemplateId(language);

        await Task.WhenAll(recipients.Select(x => client.SendEmailAsync(x, templateId, personalisation)));
    }
}
