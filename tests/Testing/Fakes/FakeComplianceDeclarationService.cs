using AutoFixture;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Services;
using Defra.WasteObligations.Testing.Fixtures.Entities;
using MongoDB.Bson;
using ComplianceDeclaration = Defra.WasteObligations.Api.Data.Entities.ComplianceDeclaration;

namespace Defra.WasteObligations.Testing.Fakes;

public class FakeComplianceDeclarationService : IComplianceDeclarationService
{
    public Func<ObjectId> CreateNewId = ObjectId.GenerateNewId;
    public Func<DateTimeOffset> UtcNow = () => DateTimeOffset.UtcNow;
    public bool ConcurrencyError = false;

    private static readonly DateTime s_start = new(2026, 4, 26, 14, 0, 0, DateTimeKind.Utc);

    public static readonly ObjectId ComplianceDeclarationId = ObjectId.Parse("6830b14f9d2a7c61f4e8b935");
    public static readonly ObjectId NonMatchingOrganisationComplianceDeclarationId = ObjectId.Parse(
        "6830af2d5b6c9f1e8a4d72c1"
    );

    private static readonly Dictionary<Guid, List<ComplianceDeclaration>> s_complianceDeclarations = new()
    {
        {
            FakeWasteOrganisationsService.OrganisationId,
            [
                ComplianceDeclarationFixture
                    .DirectProducer(FakeWasteOrganisationsService.OrganisationId)
                    .With(x => x.Id, ComplianceDeclarationId)
                    .With(x => x.Created, s_start)
                    .With(x => x.Updated, s_start)
                    .With(x => x.Audit, AuditEntryFixture.Submitted())
                    .Create(),
                ComplianceDeclarationFixture
                    .DirectProducer(FakeWasteOrganisationsService.OrganisationId)
                    .With(x => x.Created, s_start.AddDays(1))
                    .With(x => x.Updated, s_start.AddDays(1))
                    .Create(),
                ComplianceDeclarationFixture
                    .DirectProducer(FakeWasteOrganisationsService.OrganisationId)
                    .With(x => x.ObligationYear, FakeWasteOrganisationsService.Year - 1)
                    .With(x => x.Created, s_start.AddYears(-1))
                    .With(x => x.Updated, s_start.AddYears(-1))
                    .Create(),
                ComplianceDeclarationFixture
                    .DirectProducer()
                    .With(x => x.Id, NonMatchingOrganisationComplianceDeclarationId)
                    .With(x => x.Created, s_start)
                    .With(x => x.Updated, s_start)
                    .Create(),
            ]
        },
    };

    public Task<ComplianceDeclaration> Create(
        ComplianceDeclaration complianceDeclaration,
        CancellationToken cancellationToken
    )
    {
        var utcNow = UtcNow().UtcDateTime;

        return Task.FromResult(
            complianceDeclaration with
            {
                Id = CreateNewId(),
                Version = 1,
                Created = utcNow,
                Updated = utcNow,
            }
        );
    }

    public Task<ComplianceDeclaration?> Read(string id, CancellationToken cancellationToken) =>
        Task.FromResult(
            s_complianceDeclarations.SelectMany(x => x.Value).FirstOrDefault(x => x.Id == ObjectId.Parse(id))
        );

    public Task<IEnumerable<ComplianceDeclaration>> Read(
        Guid organisationId,
        int obligationYear,
        CancellationToken cancellationToken
    )
    {
        if (s_complianceDeclarations.TryGetValue(organisationId, out var complianceDeclarations))
            return Task.FromResult<IEnumerable<ComplianceDeclaration>>(
                complianceDeclarations.Where(x => x.ObligationYear == obligationYear).ToList()
            );

        return Task.FromResult(Enumerable.Empty<ComplianceDeclaration>());
    }

    public Task<bool> Delete(string id, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<ComplianceDeclarationSearchResult> Search(
        ComplianceDeclarationSearchQuery query,
        int page,
        int pageSize,
        CancellationToken cancellationToken
    )
    {
        throw new NotImplementedException();
    }

    public Task<ComplianceDeclaration> Update(
        ComplianceDeclaration current,
        ComplianceDeclaration updated,
        CancellationToken cancellationToken
    )
    {
        if (ConcurrencyError)
            throw new ConcurrencyException(
                $"Concurrency issue on write, compliance declaration with id '{current.Id}' was not updated"
            );

        if (s_complianceDeclarations.TryGetValue(updated.Organisation.Id, out var complianceDeclarations))
        {
            var index = complianceDeclarations.FindIndex(x => x.Id == updated.Id);
            if (index != -1)
            {
                return Task.FromResult(updated with { Updated = UtcNow().UtcDateTime });
            }
        }

        throw new Exception("Compliance declaration could not be found");
    }

    public Task<ComplianceDeclaration> UpdateStatus(
        ComplianceDeclaration current,
        ComplianceDeclarationStatus status,
        string? reason,
        User user,
        CancellationToken cancellationToken
    )
    {
        var updated = current.UpdateStatus(status, reason, user, UtcNow().UtcDateTime);

        return Update(current, updated, cancellationToken);
    }
}
