namespace Defra.WasteObligations.Api.Utils.Metrics;

public interface IOrganisationObligationHydrationMetrics
{
    void Failed();

    void Succeeded();

    void StalenessObserved(int staleSummaryCount, double oldestStaleSummaryAgeSeconds);
}
