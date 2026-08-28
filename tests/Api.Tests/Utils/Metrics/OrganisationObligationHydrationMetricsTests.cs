using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Utils.Metrics;
using Microsoft.Extensions.DependencyInjection;
using ApiMetrics = Defra.WasteObligations.Api.Utils.Metrics.Metrics;

namespace Defra.WasteObligations.Api.Tests.Utils.Metrics;

public class OrganisationObligationHydrationMetricsTests
{
    [Fact]
    public void FailedAndSucceeded_ShouldIncrementOutcomeCounters()
    {
        var meterFactory = CreateMeterFactory();
        using var failureCollector = new TestMetricCollector<long>(
            ApiMetrics.MeterName,
            ApiMetrics.Names.OrganisationObligationHydrationFailure
        );
        using var successCollector = new TestMetricCollector<long>(
            ApiMetrics.MeterName,
            ApiMetrics.Names.OrganisationObligationHydrationSuccess
        );
        var subject = new OrganisationObligationHydrationMetrics(meterFactory);

        subject.Failed();
        subject.Succeeded();

        var failureMeasurements = failureCollector.GetMeasurementSnapshot();
        failureMeasurements.Should().ContainSingle();
        failureMeasurements[0].Value.Should().Be(1);
        var successMeasurements = successCollector.GetMeasurementSnapshot();
        successMeasurements.Should().ContainSingle();
        successMeasurements[0].Value.Should().Be(1);
    }

    [Fact]
    public void StalenessObserved_ShouldRecordStaleSummaryCountAndOldestAge()
    {
        var meterFactory = CreateMeterFactory();
        using var ageCollector = new TestMetricCollector<double>(
            ApiMetrics.MeterName,
            ApiMetrics.Names.OrganisationObligationHydrationStaleSummaryAge
        );
        using var countCollector = new TestMetricCollector<long>(
            ApiMetrics.MeterName,
            ApiMetrics.Names.OrganisationObligationHydrationStaleSummaryCount
        );
        var subject = new OrganisationObligationHydrationMetrics(meterFactory);

        subject.StalenessObserved(2, 3600);

        var ageMeasurements = ageCollector.GetMeasurementSnapshot();
        ageMeasurements.Should().ContainSingle();
        ageMeasurements[0].Value.Should().Be(3600);
        var countMeasurements = countCollector.GetMeasurementSnapshot();
        countMeasurements.Should().ContainSingle();
        countMeasurements[0].Value.Should().Be(2);
    }

    private static IMeterFactory CreateMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        return services.BuildServiceProvider().GetRequiredService<IMeterFactory>();
    }
}
