using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;

namespace Defra.WasteObligations.Api.Services;

public interface IComplianceDeclarationService
{
    Task<ComplianceDeclaration> Create(
        ComplianceDeclaration complianceDeclaration,
        CancellationToken cancellationToken
    );

    Task<ComplianceDeclaration?> Read(string id, CancellationToken cancellationToken);

    Task<IEnumerable<ComplianceDeclaration>> Read(
        Guid organisationId,
        int obligationYear,
        CancellationToken cancellationToken
    );

    Task<bool> Delete(string id, CancellationToken cancellationToken);

    Task<ComplianceDeclarationSearchResult> Search(
        ComplianceDeclarationSearchQuery query,
        int page,
        int pageSize,
        CancellationToken cancellationToken
    );

    Task<ComplianceDeclaration> Update(
        ComplianceDeclaration current,
        ComplianceDeclaration updated,
        CancellationToken cancellationToken
    );

    Task<ComplianceDeclaration> UpdateStatus(
        ComplianceDeclaration current,
        ComplianceDeclarationStatus status,
        string? reason,
        User user,
        CancellationToken cancellationToken
    );
}
