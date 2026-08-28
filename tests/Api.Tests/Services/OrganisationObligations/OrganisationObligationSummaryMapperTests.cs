using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Dtos;
using Defra.WasteObligations.Api.Services.OrganisationObligations;
using Defra.WasteObligations.Testing.Fixtures.PrnCommonBackend;
using PrnObligation = Defra.WasteObligations.Api.Services.PrnCommonBackend.Obligation;

namespace Defra.WasteObligations.Api.Tests.Services.OrganisationObligations;

public class OrganisationObligationSummaryMapperTests
{
    private const int ObligationYear = 2026;

    [Fact]
    public void Map_WhenNoObligationsAreReturned_ShouldCreateAnEmptySuccessfulSummary()
    {
        var result = OrganisationObligationSummaryMapper.Map(ObligationFixture.OrganisationId, ObligationYear, []);

        result.ObligationCount.Should().Be(0);
        result.TotalAcceptedTonnage.Should().Be(0);
        result.TotalObligatedTonnage.Should().Be(0);
        result.RecyclingObligationsMet.Should().BeNull();
        result.ObligationCoveragePercentage.Should().Be(0);
        result.SourceFingerprint.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Map_WhenAnyMaterialIsNotMet_ShouldSetRecyclingObligationsMetToFalse()
    {
        var result = OrganisationObligationSummaryMapper.Map(
            ObligationFixture.OrganisationId,
            ObligationYear,
            [
                CreateObligation("Glass", accepted: 15, obligated: 20, ObligationStatus.Met),
                CreateObligation("Plastic", accepted: 20, obligated: 20, ObligationStatus.NotMet),
            ]
        );

        result.ObligationCount.Should().Be(2);
        result.TotalAcceptedTonnage.Should().Be(35);
        result.TotalObligatedTonnage.Should().Be(40);
        result.RecyclingObligationsMet.Should().BeFalse();
        result.ObligationCoveragePercentage.Should().Be(88);
    }

    [Fact]
    public void Map_WhenEveryMaterialHasNoDataYet_ShouldSetRecyclingObligationsMetToNull()
    {
        var result = OrganisationObligationSummaryMapper.Map(
            ObligationFixture.OrganisationId,
            ObligationYear,
            [
                CreateObligation("Glass", accepted: 0, obligated: 10, ObligationStatus.NoDataYet),
                CreateObligation("Plastic", accepted: 0, obligated: 20, ObligationStatus.NoDataYet),
            ]
        );

        result.RecyclingObligationsMet.Should().BeNull();
    }

    [Fact]
    public void Map_WhenAtLeastOneMaterialIsMetAndNoneAreNotMet_ShouldSetRecyclingObligationsMetToTrue()
    {
        var result = OrganisationObligationSummaryMapper.Map(
            ObligationFixture.OrganisationId,
            ObligationYear,
            [
                CreateObligation("Glass", accepted: 10, obligated: 10, ObligationStatus.Met),
                CreateObligation("Plastic", accepted: 0, obligated: 10, ObligationStatus.NoDataYet),
            ]
        );

        result.RecyclingObligationsMet.Should().BeTrue();
    }

    [Fact]
    public void Map_WhenTheSourceOrderDiffers_ShouldCreateTheSameFingerprint()
    {
        var glass = CreateObligation("Glass", accepted: 10, obligated: 20, ObligationStatus.Met);
        var plastic = CreateObligation("Plastic", accepted: 20, obligated: 20, ObligationStatus.Met);

        var first = OrganisationObligationSummaryMapper.Map(
            ObligationFixture.OrganisationId,
            ObligationYear,
            [glass, plastic]
        );
        var second = OrganisationObligationSummaryMapper.Map(
            ObligationFixture.OrganisationId,
            ObligationYear,
            [plastic, glass]
        );

        second.SourceFingerprint.Should().Be(first.SourceFingerprint);
    }

    [Fact]
    public void Map_WhenOptionalSourceTonnagesArePresentOrMissing_ShouldFingerprintTheirValues()
    {
        var obligation = ObligationFixture
            .Default()
            .With(x => x.ObligationToMeet, (int?)null)
            .With(x => x.TonnageOutstanding, (int?)1)
            .Create();

        var result = OrganisationObligationSummaryMapper.Map(
            ObligationFixture.OrganisationId,
            ObligationYear,
            [obligation]
        );

        result.TotalObligatedTonnage.Should().Be(0);
        result.ObligationCoveragePercentage.Should().Be(0);
        result.SourceFingerprint.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Map_WhenTheSourceStatusIsUnexpected_ShouldThrow()
    {
        var action = () =>
            OrganisationObligationSummaryMapper.Map(
                ObligationFixture.OrganisationId,
                ObligationYear,
                [CreateObligation("Glass", accepted: 10, obligated: 20, "Unknown")]
            );

        action.Should().Throw<InvalidOperationException>().WithMessage("Unexpected obligation status 'Unknown'.");
    }

    private static PrnObligation CreateObligation(string materialName, int accepted, int obligated, string status) =>
        ObligationFixture
            .Default()
            .With(x => x.MaterialName, materialName)
            .With(x => x.TonnageAccepted, accepted)
            .With(x => x.ObligationToMeet, obligated)
            .With(x => x.Status, status)
            .Create();
}
