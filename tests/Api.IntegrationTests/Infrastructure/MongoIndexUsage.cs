namespace Defra.WasteObligations.Api.IntegrationTests.Infrastructure;

public record MongoIndexUsage(string CollectionName, string IndexName, long AccessCount);
