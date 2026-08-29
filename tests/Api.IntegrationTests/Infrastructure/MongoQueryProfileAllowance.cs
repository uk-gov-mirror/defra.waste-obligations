namespace Defra.WasteObligations.Api.IntegrationTests.Infrastructure;

public record MongoQueryProfileAllowance(
    string Operation,
    string Namespace,
    string FilterShape,
    string ReviewTicket,
    string Reason
)
{
    public bool Allows(MongoQueryProfile query) =>
        query.Operation == Operation && query.Namespace == Namespace && query.FilterShape == FilterShape;
}
