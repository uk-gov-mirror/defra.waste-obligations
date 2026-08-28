using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Defra.WasteObligations.Api.Dtos.Attributes;
using Defra.WasteObligations.Api.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Defra.WasteObligations.Api.Dtos;

public record UnsubmittedComplianceDeclarationsRequest
{
    private const int SearchMaxLength = 100;

    [FromQuery(Name = "obligationYear")]
    [Range(Dtos.ObligationYear.Minimum, Dtos.ObligationYear.Maximum)]
    public int? ObligationYear { get; init; }

    [Description("Comma separated list of organisation registration type")]
    [FromQuery(Name = "registrationType")]
    [EnumCommaSeparatedList<RegistrationType>(ErrorMessage = "Invalid organisation registration type(s)")]
    public string? RegistrationType { get; init; }

    [Description("Case-insensitive partial match on organisation name or reference number")]
    [StringLength(SearchMaxLength)]
    [FromQuery(Name = "search")]
    public string? Search { get; init; }

    [Description(
        "Comma separated sort fields in priority order. Each field must use the format Field[asc] or Field[desc]. "
            + "Fields: OrganisationName, OrganisationReferenceNumber, RecyclingObligations, PercentageMet"
    )]
    [FromQuery(Name = "sort")]
    [UnsubmittedOrganisationSortList(ErrorMessage = "Invalid unsubmitted compliance declaration sort")]
    public string? Sort { get; init; }

    [Description("Page number (1-based), defaults to 1 if not specified")]
    [Minimum(Paging.MinimumPage)]
    [FromQuery(Name = "page")]
    public int? Page { get; init; }

    [Description("Number of items per page, defaults to 20 if not specified, max of 100")]
    [Range(Paging.MinimumPageSize, Paging.MaximumPageSize)]
    [FromQuery(Name = "pageSize")]
    public int? PageSize { get; init; }

    public int EffectivePage => Page ?? Paging.DefaultPage;
    public int EffectivePageSize => PageSize ?? Paging.DefaultPageSize;

    public Data.Entities.RegistrationType[] ParsedRegistrationTypes() =>
        RegistrationType?.Split(',').NotNull().Select(x => x.FromJsonValue<RegistrationType>().ToEntity()).ToArray()
        ?? [];

    public Data.UnsubmittedOrganisationSort[] ParsedSort() => UnsubmittedOrganisationSortParser.Parse(Sort);
}
