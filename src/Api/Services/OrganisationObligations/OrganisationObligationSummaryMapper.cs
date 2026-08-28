using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Dtos;
using PrnObligation = Defra.WasteObligations.Api.Services.PrnCommonBackend.Obligation;

namespace Defra.WasteObligations.Api.Services.OrganisationObligations;

public static class OrganisationObligationSummaryMapper
{
    private const string FingerprintVersion = "organisation-obligation-summary-v1";

    public static OrganisationObligationMetrics Map(
        Guid organisationId,
        int obligationYear,
        IEnumerable<PrnObligation> obligations
    )
    {
        var obligationArray = obligations
            .OrderBy(x => x.MaterialName, StringComparer.Ordinal)
            .ThenBy(x => x.Tonnage)
            .ThenBy(x => x.MaterialTarget)
            .ThenBy(x => x.ObligationToMeet)
            .ThenBy(x => x.TonnageAwaitingAcceptance)
            .ThenBy(x => x.TonnageAccepted)
            .ThenBy(x => x.TonnageOutstanding)
            .ThenBy(x => x.Status, StringComparer.Ordinal)
            .ToArray();
        ValidateStatuses(obligationArray);

        var totalAcceptedTonnage = obligationArray.Sum(x => x.TonnageAccepted);
        var totalObligatedTonnage = obligationArray.Sum(x => x.ObligationToMeet ?? 0);

        return new OrganisationObligationMetrics
        {
            ObligationCount = obligationArray.Length,
            TotalAcceptedTonnage = totalAcceptedTonnage,
            TotalObligatedTonnage = totalObligatedTonnage,
            RecyclingObligationsMet = CalculateRecyclingObligationsMet(obligationArray),
            ObligationCoveragePercentage = ObligationCoveragePercentageCalculator.Calculate(
                totalAcceptedTonnage,
                totalObligatedTonnage
            ),
            SourceFingerprint = CreateSourceFingerprint(organisationId, obligationYear, obligationArray),
        };
    }

    private static bool? CalculateRecyclingObligationsMet(PrnObligation[] obligations)
    {
        if (obligations.Length == 0 || obligations.All(x => x.Status == ObligationStatus.NoDataYet))
            return null;

        return !obligations.Any(x => x.Status == ObligationStatus.NotMet);
    }

    private static string CreateSourceFingerprint(
        Guid organisationId,
        int obligationYear,
        IEnumerable<PrnObligation> obligations
    )
    {
        var builder = new StringBuilder();
        AppendValue(builder, FingerprintVersion);
        AppendValue(builder, organisationId.ToString("D"));
        AppendValue(builder, obligationYear.ToString(CultureInfo.InvariantCulture));

        foreach (var obligation in obligations)
        {
            AppendValue(builder, obligation.MaterialName);
            AppendValue(builder, obligation.Tonnage.ToString(CultureInfo.InvariantCulture));
            AppendValue(builder, obligation.MaterialTarget.ToString(CultureInfo.InvariantCulture));
            AppendValue(builder, obligation.ObligationToMeet?.ToString(CultureInfo.InvariantCulture));
            AppendValue(builder, obligation.TonnageAwaitingAcceptance.ToString(CultureInfo.InvariantCulture));
            AppendValue(builder, obligation.TonnageAccepted.ToString(CultureInfo.InvariantCulture));
            AppendValue(builder, obligation.TonnageOutstanding?.ToString(CultureInfo.InvariantCulture));
            AppendValue(builder, obligation.Status);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendValue(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("null|");
            return;
        }

        builder.Append(value.Length).Append(':').Append(value).Append('|');
    }

    private static void ValidateStatuses(IEnumerable<PrnObligation> obligations)
    {
        var unexpectedObligation = obligations.FirstOrDefault(x =>
            !ObligationStatus.All.Contains(x.Status, StringComparer.Ordinal)
        );

        if (unexpectedObligation is not null)
            throw new InvalidOperationException($"Unexpected obligation status '{unexpectedObligation.Status}'.");
    }
}
