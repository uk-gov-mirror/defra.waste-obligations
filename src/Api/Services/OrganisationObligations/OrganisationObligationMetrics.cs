namespace Defra.WasteObligations.Api.Services.OrganisationObligations;

public record OrganisationObligationMetrics
{
    public required int ObligationCount { get; init; }
    public required int TotalAcceptedTonnage { get; init; }
    public required int TotalObligatedTonnage { get; init; }
    public required bool? RecyclingObligationsMet { get; init; }
    public required decimal ObligationCoveragePercentage { get; init; }
    public required string SourceFingerprint { get; init; }
}
