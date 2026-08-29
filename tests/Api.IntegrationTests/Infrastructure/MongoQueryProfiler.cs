using MongoDB.Bson;
using MongoDB.Driver;

namespace Defra.WasteObligations.Api.IntegrationTests.Infrastructure;

public sealed class MongoQueryProfiler : IAsyncDisposable
{
    public const string ApiApplicationName = "waste-obligations-api";
    public const string IntegrationTestApplicationName = "waste-obligations-integration-application";
    public const string IntegrationTestFixtureApplicationName = "waste-obligations-integration-fixtures";

    private const string ProfileCollectionName = "system.profile";

    private readonly IMongoDatabase _database;
    private readonly IReadOnlyCollection<string> _applicationNames;
    private readonly IReadOnlyCollection<MongoIndexUsage> _indexUsageAtStart;
    private bool _stopped;

    private MongoQueryProfiler(
        IMongoDatabase database,
        IReadOnlyCollection<string> applicationNames,
        IReadOnlyCollection<MongoIndexUsage> indexUsageAtStart
    )
    {
        _database = database;
        _applicationNames = applicationNames;
        _indexUsageAtStart = indexUsageAtStart;
    }

    public static async Task<MongoQueryProfiler> Start(
        IMongoDatabase database,
        IReadOnlyCollection<string> applicationNames,
        CancellationToken cancellationToken
    )
    {
        await database.RunCommandAsync<BsonDocument>(
            new BsonDocument("profile", 0),
            cancellationToken: cancellationToken
        );
        await DropProfileCollection(database, cancellationToken);

        var indexUsageAtStart = await ReadIndexUsage(database, cancellationToken);
        await database.RunCommandAsync<BsonDocument>(
            new BsonDocument { { "profile", 1 }, { "filter", ApplicationFilter(applicationNames) } },
            cancellationToken: cancellationToken
        );

        return new MongoQueryProfiler(database, applicationNames, indexUsageAtStart);
    }

    public async Task<MongoQueryProfileReport> Stop(CancellationToken cancellationToken)
    {
        if (_stopped)
            throw new InvalidOperationException("The MongoDB query profiler has already stopped.");

        await _database.RunCommandAsync<BsonDocument>(
            new BsonDocument { { "profile", 0 }, { "filter", "unset" } },
            cancellationToken: cancellationToken
        );
        _stopped = true;

        var queries = await ReadQueries(cancellationToken);
        var indexUsageAtEnd = await ReadIndexUsage(_database, cancellationToken);

        return new MongoQueryProfileReport(queries, UnusedSecondaryIndexes(_indexUsageAtStart, indexUsageAtEnd));
    }

    public async ValueTask DisposeAsync()
    {
        if (_stopped)
            return;

        await _database.RunCommandAsync<BsonDocument>(new BsonDocument { { "profile", 0 }, { "filter", "unset" } });
        _stopped = true;
    }

    private static async Task DropProfileCollection(IMongoDatabase database, CancellationToken cancellationToken)
    {
        var profileCollection = database.GetCollection<BsonDocument>(ProfileCollectionName);
        await profileCollection.Database.DropCollectionAsync(ProfileCollectionName, cancellationToken);
    }

    private async Task<IReadOnlyCollection<MongoQueryProfile>> ReadQueries(CancellationToken cancellationToken)
    {
        var entries = await _database
            .GetCollection<BsonDocument>(ProfileCollectionName)
            .Find(ApplicationFilter(_applicationNames))
            .ToListAsync(cancellationToken);

        return entries.Where(x => x.Contains("planSummary")).Select(ToQueryProfile).ToArray();
    }

    private static BsonDocument ApplicationFilter(IReadOnlyCollection<string> applicationNames) =>
        applicationNames.Count == 1
            ? new BsonDocument("appName", applicationNames.Single())
            : new BsonDocument("appName", new BsonDocument("$in", new BsonArray(applicationNames)));

    private static MongoQueryProfile ToQueryProfile(BsonDocument entry)
    {
        var planSummary = entry["planSummary"].AsString;

        return new MongoQueryProfile(
            entry["op"].AsString,
            entry["ns"].AsString,
            entry.GetValue("command", new BsonDocument()).AsBsonDocument,
            planSummary,
            entry.GetValue("keysExamined", 0).ToInt64(),
            entry.GetValue("docsExamined", 0).ToInt64(),
            IndexNames(entry.GetValue("execStats", new BsonDocument()))
        );
    }

    private static IReadOnlyCollection<string> IndexNames(BsonValue value)
    {
        var indexNames = new HashSet<string>(StringComparer.Ordinal);
        FindIndexNames(value, indexNames);

        return indexNames;
    }

    private static void FindIndexNames(BsonValue value, ISet<string> indexNames)
    {
        if (value is BsonArray array)
        {
            foreach (var item in array)
            {
                FindIndexNames(item, indexNames);
            }

            return;
        }

        if (value is not BsonDocument document)
            return;

        if (
            document.TryGetValue("stage", out var stage)
            && stage.IsString
            && document.TryGetValue("indexName", out var indexName)
            && indexName.IsString
            && stage.AsString is "IXSCAN" or "COUNT_SCAN" or "DISTINCT_SCAN"
        )
        {
            indexNames.Add(indexName.AsString);
        }

        foreach (var element in document)
        {
            FindIndexNames(element.Value, indexNames);
        }
    }

    private static async Task<IReadOnlyCollection<MongoIndexUsage>> ReadIndexUsage(
        IMongoDatabase database,
        CancellationToken cancellationToken
    )
    {
        using var cursor = await database.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        var collections = await cursor.ToListAsync(cancellationToken);
        var usages = new List<MongoIndexUsage>();

        foreach (var collectionName in collections.Where(x => !x.StartsWith("system.", StringComparison.Ordinal)))
        {
            var response = await database.RunCommandAsync<BsonDocument>(
                new BsonDocument
                {
                    { "aggregate", collectionName },
                    { "pipeline", new BsonArray([new BsonDocument("$indexStats", new BsonDocument())]) },
                    { "cursor", new BsonDocument() },
                },
                cancellationToken: cancellationToken
            );
            var indexStats = response["cursor"].AsBsonDocument["firstBatch"].AsBsonArray;

            usages.AddRange(
                indexStats.Select(x =>
                {
                    var index = x.AsBsonDocument;

                    return new MongoIndexUsage(
                        collectionName,
                        index["name"].AsString,
                        index["accesses"].AsBsonDocument["ops"].ToInt64()
                    );
                })
            );
        }

        return usages;
    }

    private static IReadOnlyCollection<MongoIndexUsage> UnusedSecondaryIndexes(
        IReadOnlyCollection<MongoIndexUsage> indexUsageAtStart,
        IReadOnlyCollection<MongoIndexUsage> indexUsageAtEnd
    )
    {
        var startByIndex = indexUsageAtStart.ToDictionary(x => (x.CollectionName, x.IndexName));

        return indexUsageAtEnd
            .Where(x =>
                x.IndexName != "_id_"
                && x.AccessCount == startByIndex.GetValueOrDefault((x.CollectionName, x.IndexName))?.AccessCount
            )
            .ToArray();
    }
}
