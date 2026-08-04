using Defra.WasteObligations.Api.Data.Entities;
using RegistrationType = Defra.WasteObligations.Api.Data.Entities.RegistrationType;

namespace Defra.WasteObligations.Api.Services;

public static class ComplianceDeclarationCancellationEmailPersonalisation
{
    private const string NaturalResourcesWalesEnglish = "Natural Resources Wales";
    private const string NaturalResourcesWalesWelsh = "Cyfoeth Naturiol Cymru (CNC)";

    public static string GetCertOrStatement(RegistrationType registrationType) =>
        registrationType is RegistrationType.ComplianceScheme ? "statement" : "certificate";

    public static string GetCertOrStatementWelsh(RegistrationType registrationType) =>
        registrationType is RegistrationType.ComplianceScheme ? "datganiad" : "tystysgrif";

    public static string GetEnvironmentalRegulatorWelsh(string environmentalRegulator) =>
        string.Equals(environmentalRegulator, NaturalResourcesWalesEnglish, StringComparison.OrdinalIgnoreCase)
            ? NaturalResourcesWalesWelsh
            : environmentalRegulator;
}
