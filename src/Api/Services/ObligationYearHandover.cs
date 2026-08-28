namespace Defra.WasteObligations.Api.Services;

public record ObligationYearHandover(
    int CurrentObligationYear,
    int? IncomingObligationYear = null,
    int? OutgoingObligationYear = null,
    DateTime? OutgoingYearCutoverAt = null
);
