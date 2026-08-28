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
    [InlineData("OrganisationName[ascending]")]
    [InlineData("DateSubmitted[asc]")]
    [InlineData("OrganisationName[asc],OrganisationName[desc]")]
    public void TryParse_WhenSortIsInvalid_ShouldReturnFalse(string value)
    {
        var parsed = UnsubmittedOrganisationSortParser.TryParse(value, out var sort);

        parsed.Should().BeFalse();
        sort.Should().BeEmpty();
    }

    [Theory]
    [InlineData("OrganisationName[asc]", UnsubmittedOrganisationSortField.OrganisationName)]
    [InlineData("OrganisationReferenceNumber[desc]", UnsubmittedOrganisationSortField.OrganisationReferenceNumber)]
    [InlineData("RecyclingObligations[asc]", UnsubmittedOrganisationSortField.RecyclingObligations)]
    [InlineData("PercentageMet[desc]", UnsubmittedOrganisationSortField.PercentageMet)]
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
            "PercentageMet[desc],OrganisationName[asc],OrganisationReferenceNumber[desc]"
        );

        sort.Should()
            .BeEquivalentTo(
                [
                    new UnsubmittedOrganisationSort
                    {
                        Field = UnsubmittedOrganisationSortField.PercentageMet,
                        Direction = UnsubmittedOrganisationSortDirection.Descending,
                    },
                    new UnsubmittedOrganisationSort
                    {
                        Field = UnsubmittedOrganisationSortField.OrganisationName,
                        Direction = UnsubmittedOrganisationSortDirection.Ascending,
                    },
                    new UnsubmittedOrganisationSort
                    {
                        Field = UnsubmittedOrganisationSortField.OrganisationReferenceNumber,
                        Direction = UnsubmittedOrganisationSortDirection.Descending,
                    },
                ],
                options => options.WithStrictOrdering()
            );
    }
}
