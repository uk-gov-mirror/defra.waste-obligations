using AwesomeAssertions;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Dtos;

namespace Defra.WasteObligations.Api.Tests.Dtos;

public class UnsubmittedOrganisationSortParserTests
{
    [Fact]
    public void Parse_WhenNotSpecified_ShouldReturnAnEmptySort()
    {
        var sort = UnsubmittedOrganisationSortParser.Parse(null);

        sort.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Name[ascending]")]
    [InlineData("OrganisationName[asc]")]
    [InlineData("OrganisationReferenceNumber[asc]")]
    [InlineData("RecyclingObligations[asc]")]
    [InlineData("PercentageMet[asc]")]
    [InlineData("DateSubmitted[asc]")]
    [InlineData("Name[asc],Name[desc]")]
    public void TryParse_WhenSortIsInvalid_ShouldReturnFalse(string value)
    {
        var parsed = UnsubmittedOrganisationSortParser.TryParse(value, out var sort);

        parsed.Should().BeFalse();
        sort.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Name[asc]", UnsubmittedOrganisationSortField.Name)]
    [InlineData("ReferenceNumber[desc]", UnsubmittedOrganisationSortField.ReferenceNumber)]
    [InlineData("RecyclingObligationsMet[asc]", UnsubmittedOrganisationSortField.RecyclingObligationsMet)]
    [InlineData("ObligationCoveragePercentage[desc]", UnsubmittedOrganisationSortField.ObligationCoveragePercentage)]
    public void Parse_WhenSortIsValid_ShouldReturnUnsubmittedOrganisationSort(
        string value,
        UnsubmittedOrganisationSortField field
    )
    {
        var sort = UnsubmittedOrganisationSortParser.Parse(value);

        sort.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new UnsubmittedOrganisationSort
                {
                    Field = field,
                    Direction = value.EndsWith("[asc]")
                        ? UnsubmittedOrganisationSortDirection.Ascending
                        : UnsubmittedOrganisationSortDirection.Descending,
                }
            );
    }

    [Fact]
    public void Parse_WhenMultipleSortFieldsAreValid_ShouldPreservePriorityOrder()
    {
        var sort = UnsubmittedOrganisationSortParser.Parse(
            "ObligationCoveragePercentage[desc],Name[asc],ReferenceNumber[desc]"
        );

        sort.Should()
            .BeEquivalentTo(
                [
                    new UnsubmittedOrganisationSort
                    {
                        Field = UnsubmittedOrganisationSortField.ObligationCoveragePercentage,
                        Direction = UnsubmittedOrganisationSortDirection.Descending,
                    },
                    new UnsubmittedOrganisationSort
                    {
                        Field = UnsubmittedOrganisationSortField.Name,
                        Direction = UnsubmittedOrganisationSortDirection.Ascending,
                    },
                    new UnsubmittedOrganisationSort
                    {
                        Field = UnsubmittedOrganisationSortField.ReferenceNumber,
                        Direction = UnsubmittedOrganisationSortDirection.Descending,
                    },
                ],
                options => options.WithStrictOrdering()
            );
    }
}
