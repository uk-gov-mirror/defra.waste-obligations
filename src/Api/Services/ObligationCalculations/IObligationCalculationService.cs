using Defra.WasteObligations.Api.Data.Entities;

namespace Defra.WasteObligations.Api.Services.ObligationCalculations;

public interface IObligationCalculationService
{
    /// <summary>
    /// Saves calculation data using the current version read during this save attempt.
    /// The incoming ID and version are ignored; they do not enforce caller-side concurrency.
    /// A conflict must be retried by the caller with a fresh save attempt.
    /// </summary>
    Task<CalculationSaveResult> SaveWithOutcome(ObligationCalculation calculation, CancellationToken cancellationToken);

    Task<ObligationCalculation> Save(ObligationCalculation calculation, CancellationToken cancellationToken);
}

public enum CalculationSaveOutcome
{
    Created,
    Updated,
    Unchanged,
}

public sealed record CalculationSaveResult(ObligationCalculation Calculation, CalculationSaveOutcome Outcome);
