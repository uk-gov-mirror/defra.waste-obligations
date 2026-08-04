using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Services.AccountBackend;
using Defra.WasteObligations.Api.Services.GovukNotify;
using Defra.WasteObligations.Api.Utils.Metrics;
using BusinessCountry = Defra.WasteObligations.Api.Services.WasteOrganisations.BusinessCountry;
using Organisation = Defra.WasteObligations.Api.Services.WasteOrganisations.Organisation;
using RegistrationType = Defra.WasteObligations.Api.Data.Entities.RegistrationType;

namespace Defra.WasteObligations.Api.Services;

public class EmailService(
    IGovukNotifyService govukNotifyService,
    IAccountBackendService accountBackendService,
    IEmailMetrics emailMetrics,
    ILogger<EmailService> logger
) : IEmailService
{
    public async Task SendSubmittedEmail(
        ComplianceDeclaration complianceDeclaration,
        Organisation organisation,
        CancellationToken cancellationToken
    )
    {
        if (complianceDeclaration.Organisation.Id != organisation.Id)
            throw new InvalidOperationException("Organisations do not match");

        var template =
            complianceDeclaration.Organisation.RegistrationType is RegistrationType.ComplianceScheme
                ? GovukNotifyOptions.TemplateName.ComplianceDeclarationSubmissionComplianceScheme
                : GovukNotifyOptions.TemplateName.ComplianceDeclarationSubmissionDirectProducer;
        var templateName = template.ToString();
        var isWales = organisation.BusinessCountry == BusinessCountry.Wales;
        var language = isWales ? "cy" : "en";
        var startingTimestamp = TimeProvider.System.GetTimestamp();
        emailMetrics.SendStarted(templateName, language);

        try
        {
            var submittedAuditEntry = complianceDeclaration.Audit.First(x =>
                x.Action == nameof(ComplianceDeclarationStatus.Submitted)
            );
            var regulator = complianceDeclaration.Organisation.Regulator;
            var personalisation = new Dictionary<string, object>
            {
                { "obligationYear", complianceDeclaration.ObligationYear },
                { "regulatorLeading", isWales ? regulator : $"The {regulator}" },
                { "regulatorInline", isWales ? regulator : $"the {regulator}" },
                { "regulatorEmail", complianceDeclaration.Organisation.RegulatorEmail },
                { "user", submittedAuditEntry.User.Name },
            };

            logger.LogInformation("Sending submitted email to submitter email address");

            await govukNotifyService.SendComplianceDeclarationSubmittedEmail(
                template,
                [submittedAuditEntry.User.Email],
                personalisation,
                language
            );

            logger.LogInformation("Sent submitted email");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Submitted email could not be sent");
            emailMetrics.SendFaulted(templateName, language, exception);

            // intentionally swallowed as failure to send an email should not break anything
        }
        finally
        {
            emailMetrics.SendCompleted(
                templateName,
                language,
                TimeProvider.System.GetElapsedTime(startingTimestamp).TotalMilliseconds
            );
        }
    }

    public async Task SendCancelledEmail(
        ComplianceDeclaration complianceDeclaration,
        Organisation organisation,
        string reason,
        CancellationToken cancellationToken
    )
    {
        if (complianceDeclaration.Organisation.Id != organisation.Id)
            throw new InvalidOperationException("Organisations do not match");

        var template = ComplianceDeclarationCancellationReasons.TryGetTemplate(reason);
        if (template is null)
        {
            logger.LogWarning("Cancellation email was not sent because the reason is not recognised: {Reason}", reason);

            return;
        }

        var templateName = template.Value.ToString();
        var isWales = organisation.BusinessCountry == BusinessCountry.Wales;
        var language = isWales ? "cy" : "en";
        var startingTimestamp = TimeProvider.System.GetTimestamp();
        emailMetrics.SendStarted(templateName, language);

        try
        {
            var personEmails = await accountBackendService.ReadPersonEmails(
                organisation.Id,
                GetEntityTypeCode(complianceDeclaration.Organisation.RegistrationType),
                cancellationToken
            );
            var recipients = personEmails.DistinctBy(x => x.Email).ToArray();
            if (recipients.Length == 0)
            {
                logger.LogWarning(
                    "Cancellation email was not sent because no recipient email addresses were returned for organisation {OrganisationId}",
                    organisation.Id
                );

                return;
            }

            logger.LogInformation(
                "Sending cancellation email for reason {Reason} to {RecipientCount} recipient email addresses",
                reason,
                recipients.Length
            );

            var cancellationRecipients = recipients
                .Select(recipient =>
                    (recipient.Email, BuildCancellationPersonalisation(complianceDeclaration, recipient))
                )
                .ToArray();

            await govukNotifyService.SendComplianceDeclarationCancelledEmail(
                template.Value,
                cancellationRecipients,
                language
            );

            logger.LogInformation("Sent cancellation email");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Cancellation email could not be sent");
            emailMetrics.SendFaulted(templateName, language, exception);
        }
        finally
        {
            emailMetrics.SendCompleted(
                templateName,
                language,
                TimeProvider.System.GetElapsedTime(startingTimestamp).TotalMilliseconds
            );
        }
    }

    private static EntityTypeCode GetEntityTypeCode(RegistrationType registrationType) =>
        registrationType switch
        {
            RegistrationType.ComplianceScheme => EntityTypeCode.CS,
            _ => EntityTypeCode.DR,
        };

    private static Dictionary<string, object> BuildCancellationPersonalisation(
        ComplianceDeclaration complianceDeclaration,
        PersonEmail recipient
    )
    {
        var registrationType = complianceDeclaration.Organisation.RegistrationType;
        var environmentalRegulator = complianceDeclaration.Organisation.Regulator;

        return new Dictionary<string, object>
        {
            {
                "certOrStatement",
                ComplianceDeclarationCancellationEmailPersonalisation.GetCertOrStatement(registrationType)
            },
            {
                "certOrStatement_Welsh",
                ComplianceDeclarationCancellationEmailPersonalisation.GetCertOrStatementWelsh(registrationType)
            },
            { "year", complianceDeclaration.ObligationYear },
            { "environmentalRegulator", environmentalRegulator },
            {
                "environmentalRegulator_Welsh",
                ComplianceDeclarationCancellationEmailPersonalisation.GetEnvironmentalRegulatorWelsh(
                    environmentalRegulator
                )
            },
            { "regulatorEmail", complianceDeclaration.Organisation.RegulatorEmail },
            { "firstName", recipient.FirstName },
            { "lastName", recipient.LastName },
        };
    }
}
