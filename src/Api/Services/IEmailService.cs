using Defra.WasteObligations.Api.Data.Entities;
using Organisation = Defra.WasteObligations.Api.Services.WasteOrganisations.Organisation;

namespace Defra.WasteObligations.Api.Services;

public interface IEmailService
{
    Task SendSubmittedEmail(
        ComplianceDeclaration complianceDeclaration,
        Organisation organisation,
        CancellationToken cancellationToken
    );

    Task SendCancelledEmail(
        ComplianceDeclaration complianceDeclaration,
        Organisation organisation,
        string reason,
        CancellationToken cancellationToken
    );
}
