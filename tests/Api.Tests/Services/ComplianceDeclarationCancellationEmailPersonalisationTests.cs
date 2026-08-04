using AwesomeAssertions;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Services;

namespace Defra.WasteObligations.Api.Tests.Services;

public class ComplianceDeclarationCancellationEmailPersonalisationTests
{
    [Theory]
    [InlineData(RegistrationType.DirectProducer, "certificate", "tystysgrif")]
    [InlineData(RegistrationType.ComplianceScheme, "statement", "datganiad")]
    public void GetCertOrStatement_ShouldReturnEnglishAndWelshValues(
        RegistrationType registrationType,
        string expectedEnglish,
        string expectedWelsh
    )
    {
        ComplianceDeclarationCancellationEmailPersonalisation
            .GetCertOrStatement(registrationType)
            .Should()
            .Be(expectedEnglish);
        ComplianceDeclarationCancellationEmailPersonalisation
            .GetCertOrStatementWelsh(registrationType)
            .Should()
            .Be(expectedWelsh);
    }

    [Theory]
    [InlineData("Natural Resources Wales", "Cyfoeth Naturiol Cymru (CNC)")]
    [InlineData("Environment Agency", "Environment Agency")]
    [InlineData("Regulator", "Regulator")]
    public void GetEnvironmentalRegulatorWelsh_ShouldOnlyTranslateNaturalResourcesWales(
        string environmentalRegulator,
        string expectedWelsh
    )
    {
        ComplianceDeclarationCancellationEmailPersonalisation
            .GetEnvironmentalRegulatorWelsh(environmentalRegulator)
            .Should()
            .Be(expectedWelsh);
    }
}
