using Defra.WasteObligations.Api.Data;

namespace Defra.WasteObligations.Api.Dtos;

public static class UnsubmittedOrganisationSortParser
{
    public static UnsubmittedOrganisationSort[] Parse(string? value)
    {
        if (value is null)
            return [];

        if (!TryParse(value, out var sort))
            throw new ArgumentException("Invalid unsubmitted organisation sort", nameof(value));

        return sort;
    }

    public static bool TryParse(string value, out UnsubmittedOrganisationSort[] sort)
    {
        sort = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var fields = new HashSet<UnsubmittedOrganisationSortField>();
        var parsedSort = new List<UnsubmittedOrganisationSort>();

        foreach (var term in value.Split(','))
        {
            var openingBracket = term.IndexOf('[');
            if (openingBracket <= 0 || !term.EndsWith(']'))
                return false;

            var fieldValue = term[..openingBracket];
            if (
                !Enum.TryParse<UnsubmittedOrganisationSortField>(fieldValue, out var field)
                || !Enum.IsDefined(field)
                || fieldValue != field.ToString()
                || !fields.Add(field)
            )
                return false;

            var direction = term[(openingBracket + 1)..^1] switch
            {
                "asc" => UnsubmittedOrganisationSortDirection.Ascending,
                "desc" => UnsubmittedOrganisationSortDirection.Descending,
                _ => (UnsubmittedOrganisationSortDirection?)null,
            };
            if (direction is null)
                return false;

            parsedSort.Add(new UnsubmittedOrganisationSort { Field = field, Direction = direction.Value });
        }

        sort = [.. parsedSort];

        return true;
    }
}
