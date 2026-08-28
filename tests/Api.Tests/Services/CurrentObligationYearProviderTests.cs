using System.Globalization;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Services;
using Microsoft.Extensions.Time.Testing;

namespace Defra.WasteObligations.Api.Tests.Services;

public class CurrentObligationYearProviderTests
{
    [Theory]
    [InlineData("2027-01-01T00:00:00+00:00", 2026)]
    [InlineData("2027-01-31T23:59:59+00:00", 2026)]
    [InlineData("2027-02-01T00:00:00+00:00", 2027)]
    [InlineData("2027-08-27T12:00:00+01:00", 2027)]
    public void GetCurrentObligationYear_ShouldUseTheUnitedKingdomObligationYear(string utcNow, int expected)
    {
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse(utcNow, CultureInfo.InvariantCulture));
        var subject = new CurrentObligationYearProvider(timeProvider);

        var result = subject.GetCurrentObligationYear();

        result.Should().Be(expected);
    }

    [Fact]
    public void GetHandover_DuringJanuary_ShouldIncludeTheIncomingObligationYear()
    {
        var timeProvider = new FakeTimeProvider(
            DateTimeOffset.Parse("2027-01-15T12:00:00+00:00", CultureInfo.InvariantCulture)
        );
        var subject = new CurrentObligationYearProvider(timeProvider);

        var result = subject.GetHandover(TimeSpan.FromHours(1));

        result.CurrentObligationYear.Should().Be(2026);
        result.IncomingObligationYear.Should().Be(2027);
        result.OutgoingObligationYear.Should().BeNull();
        result.OutgoingYearCutoverAt.Should().BeNull();
    }

    [Fact]
    public void GetHandover_DuringOutgoingYearGrace_ShouldIncludeTheOutgoingObligationYear()
    {
        var timeProvider = new FakeTimeProvider(
            DateTimeOffset.Parse("2027-02-01T00:30:00+00:00", CultureInfo.InvariantCulture)
        );
        var subject = new CurrentObligationYearProvider(timeProvider);

        var result = subject.GetHandover(TimeSpan.FromHours(1));

        result.CurrentObligationYear.Should().Be(2027);
        result.IncomingObligationYear.Should().BeNull();
        result.OutgoingObligationYear.Should().Be(2026);
        result.OutgoingYearCutoverAt.Should().Be(new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void GetHandover_AfterOutgoingYearGrace_ShouldOnlyIncludeTheCurrentObligationYear()
    {
        var timeProvider = new FakeTimeProvider(
            DateTimeOffset.Parse("2027-02-01T01:00:00+00:00", CultureInfo.InvariantCulture)
        );
        var subject = new CurrentObligationYearProvider(timeProvider);

        var result = subject.GetHandover(TimeSpan.FromHours(1));

        result.CurrentObligationYear.Should().Be(2027);
        result.IncomingObligationYear.Should().BeNull();
        result.OutgoingObligationYear.Should().BeNull();
        result.OutgoingYearCutoverAt.Should().BeNull();
    }
}
