namespace Defra.WasteObligations.Api.Endpoints.ComplianceDeclarations;

public static class ComplianceDeclarationEndpoints
{
    public static void MapComplianceDeclarationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapComplianceDeclarationsSearch();
        app.MapUnsubmittedComplianceDeclarationsSearch();
        app.MapComplianceDeclarationDelete();
    }
}
