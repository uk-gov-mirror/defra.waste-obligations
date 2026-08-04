using AutoFixture;
using AutoFixture.Dsl;
using Defra.WasteObligations.Api.Dtos;

namespace Defra.WasteObligations.Testing.Fixtures.Dtos;

public static class UpdateComplianceDeclarationRequestFixture
{
    private static Fixture GetFixture() => new();

    public static IPostprocessComposer<UpdateComplianceDeclarationRequest> AddDefaults(
        this ICustomizationComposer<UpdateComplianceDeclarationRequest> composer
    )
    {
        return composer;
    }

    public static IPostprocessComposer<UpdateComplianceDeclarationRequest> Request()
    {
        return GetFixture().Build<UpdateComplianceDeclarationRequest>().AddDefaults();
    }

    public static IPostprocessComposer<UpdateComplianceDeclarationRequest> Default()
    {
        return Request()
            .With(x => x.Status, ComplianceDeclarationStatus.Submitted)
            .With(x => x.User, UserFixture.Default().Create());
    }

    public static IPostprocessComposer<UpdateComplianceDeclarationRequest> Accepted()
    {
        return Default()
            .With(x => x.Status, ComplianceDeclarationStatus.Accepted)
            .With(x => x.Reason, (string?)null)
            .With(x => x.User, UserFixture.Regulator().Create());
    }

    public static IPostprocessComposer<UpdateComplianceDeclarationRequest> Cancelled(string? reason = null)
    {
        return Default()
            .With(x => x.Status, ComplianceDeclarationStatus.Cancelled)
            .With(x => x.Reason, reason ?? "Producer requested to cancel")
            .With(x => x.User, UserFixture.ApprovedPerson().Create());
    }
}
