using System.ComponentModel.DataAnnotations;

namespace Defra.WasteObligations.Api.Services.GovukNotify;

public class GovukNotifyOptions
{
    public const string SectionName = "GovukNotify";

    [Required]
    public required string ApiKey { get; init; }

    public string? BaseAddress { get; init; }

    public Dictionary<TemplateName, Template> Templates { get; init; } = new();

    public class Template
    {
        [Required]
        public required TemplateId TemplateId { get; init; }

        public string GetTemplateId(string language) => language == "cy" ? TemplateId.Cy : TemplateId.En;
    }

    public class TemplateId
    {
        [Required]
        public required string En { get; init; }

        [Required]
        public required string Cy { get; init; }
    }

    public enum TemplateName
    {
        ComplianceDeclarationSubmissionDirectProducer,
        ComplianceDeclarationSubmissionComplianceScheme,
        ComplianceDeclarationCancellationNotSignedByCorrectPerson,
        ComplianceDeclarationCancellationRecyclingObligationsChanged,
        ComplianceDeclarationCancellationCanMeetRecyclingObligations,
        ComplianceDeclarationCancellationProducerRequested,
    }
}
