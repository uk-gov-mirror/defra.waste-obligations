using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Utils.Logging;
using Defra.WasteObligations.Api.Utils.Metrics;
using Defra.WasteObligations.AuditEvents;
using Microsoft.AspNetCore.HeaderPropagation;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace Defra.WasteObligations.Api.Services;

public class ComplianceDeclarationService(
    IDbContext dbContext,
    ILogger<ComplianceDeclarationService> logger,
    TimeProvider timeProvider,
    IAuditEventService auditEventService,
    IComplianceDeclarationMetrics complianceDeclarationMetrics,
    HeaderPropagationValues headerPropagationValues,
    IOptions<TraceHeader> traceHeaderOptions
) : IComplianceDeclarationService
{
    private const string Actor = "service:waste-obligations";
    private const string ComplianceDeclarationEntity = "compliance_declaration";

    public async Task<ComplianceDeclaration> Create(
        ComplianceDeclaration complianceDeclaration,
        CancellationToken cancellationToken
    )
    {
        var utcNow = timeProvider.GetUtcNowWithoutMicroseconds();
        complianceDeclaration = complianceDeclaration with { Version = 1, Created = utcNow, Updated = utcNow };

        using var session = await dbContext.StartSession(cancellationToken);
        session.StartTransaction();

        try
        {
            await dbContext.ComplianceDeclarations.InsertOneAsync(
                session,
                complianceDeclaration,
                cancellationToken: cancellationToken
            );

            await auditEventService.RecordEvent(
                session,
                new AuditEventRequest(
                    Actor,
                    ComplianceDeclarationEntity,
                    AuditEventOperation.Insert,
                    "submission.created",
                    null,
                    complianceDeclaration.Id.ToString(),
                    complianceDeclaration.Version,
                    null,
                    complianceDeclaration.ToBsonDocument(),
                    complianceDeclaration.SchemaVersion,
                    utcNow,
                    ReadTraceId()
                ),
                cancellationToken
            );

            await session.CommitTransactionAsync(cancellationToken);
        }
        catch
        {
            await session.AbortTransactionAsync(CancellationToken.None);
            throw;
        }

        complianceDeclarationMetrics.Created();
        logger.LogInformation(
            "Created compliance declaration with id '{ComplianceDeclarationId}'",
            complianceDeclaration.Id
        );

        return complianceDeclaration;
    }

    public async Task<ComplianceDeclaration?> Read(string id, CancellationToken cancellationToken) =>
        await dbContext
            .ComplianceDeclarations.Find(Builders<ComplianceDeclaration>.Filter.Eq(x => x.Id, ObjectId.Parse(id)))
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

    public async Task<IEnumerable<ComplianceDeclaration>> Read(
        Guid organisationId,
        int obligationYear,
        CancellationToken cancellationToken
    ) =>
        await dbContext
            .ComplianceDeclarations.AsQueryable()
            .Where(x => x.Organisation.Id == organisationId && x.ObligationYear == obligationYear)
            .ToListAsync(cancellationToken);

    public async Task<bool> Delete(string id, CancellationToken cancellationToken)
    {
        using var session = await dbContext.StartSession(cancellationToken);
        session.StartTransaction();

        try
        {
            var objectId = ObjectId.Parse(id);
            var current = await dbContext
                .ComplianceDeclarations.Find(session, Builders<ComplianceDeclaration>.Filter.Eq(x => x.Id, objectId))
                .FirstOrDefaultAsync(cancellationToken: cancellationToken);

            if (current is null)
            {
                await session.AbortTransactionAsync(cancellationToken);

                return false;
            }

            var deleteFilter = Builders<ComplianceDeclaration>.Filter.And(
                Builders<ComplianceDeclaration>.Filter.Eq(x => x.Id, objectId),
                Builders<ComplianceDeclaration>.Filter.Eq(x => x.Version, current.Version)
            );

            var deleteResult = await dbContext.ComplianceDeclarations.DeleteOneAsync(
                session,
                deleteFilter,
                null,
                cancellationToken
            );

            if (deleteResult.DeletedCount == 0)
                throw new ConcurrencyException(
                    $"Concurrency issue on delete, compliance declaration with id '{current.Id}' was not deleted"
                );

            var utcNow = timeProvider.GetUtcNowWithoutMicroseconds();
            await auditEventService.RecordEvent(
                session,
                new AuditEventRequest(
                    Actor,
                    ComplianceDeclarationEntity,
                    AuditEventOperation.Delete,
                    "submission.removed",
                    "elevated system allowed removal",
                    current.Id.ToString(),
                    current.Version + 1,
                    current.ToBsonDocument(),
                    null,
                    current.SchemaVersion,
                    utcNow,
                    ReadTraceId()
                ),
                cancellationToken
            );

            await session.CommitTransactionAsync(cancellationToken);
        }
        catch
        {
            await session.AbortTransactionAsync(CancellationToken.None);
            throw;
        }

        complianceDeclarationMetrics.Deleted();
        logger.LogInformation("Deleted compliance declaration with id '{ComplianceDeclarationId}'", id);

        return true;
    }

    public async Task<ComplianceDeclarationSearchResult> Search(
        ComplianceDeclarationSearchQuery query,
        int page,
        int pageSize,
        CancellationToken cancellationToken
    )
    {
        var filters = new List<FilterDefinition<ComplianceDeclaration>>();

        if (query.ObligationYear.HasValue)
        {
            filters.Add(Builders<ComplianceDeclaration>.Filter.Eq(x => x.ObligationYear, query.ObligationYear.Value));
        }

        if (query.Status is { Length: > 0 })
        {
            filters.Add(Builders<ComplianceDeclaration>.Filter.In(x => x.Status, query.Status));
        }

        if (query.RegistrationType is { Length: > 0 })
        {
            filters.Add(
                Builders<ComplianceDeclaration>.Filter.In(x => x.Organisation.RegistrationType, query.RegistrationType)
            );
        }

        if (!string.IsNullOrWhiteSpace(query.OrganisationName))
        {
            filters.Add(
                Builders<ComplianceDeclaration>.Filter.Regex(
                    x => x.Organisation.Name,
                    new BsonRegularExpression(System.Text.RegularExpressions.Regex.Escape(query.OrganisationName), "i")
                )
            );
        }

        var combinedFilter =
            filters.Count == 0
                ? Builders<ComplianceDeclaration>.Filter.Empty
                : Builders<ComplianceDeclaration>.Filter.And(filters);

        var countTask = dbContext.ComplianceDeclarations.CountDocumentsAsync(
            combinedFilter,
            cancellationToken: cancellationToken
        );
        var resultsTask = dbContext
            .ComplianceDeclarations.Find(combinedFilter)
            .SortBy(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(cancellationToken);

        await Task.WhenAll(countTask, resultsTask);

        return new ComplianceDeclarationSearchResult
        {
            ComplianceDeclarations = resultsTask.Result,
            Total = (int)countTask.Result,
        };
    }

    public async Task<ComplianceDeclaration> Update(
        ComplianceDeclaration current,
        ComplianceDeclaration updated,
        CancellationToken cancellationToken
    )
    {
        using var session = await dbContext.StartSession(cancellationToken);
        session.StartTransaction();

        var filter = Builders<ComplianceDeclaration>.Filter.And(
            Builders<ComplianceDeclaration>.Filter.Eq(x => x.Id, current.Id),
            Builders<ComplianceDeclaration>.Filter.Eq(x => x.Version, current.Version)
        );

        updated = updated with { Version = current.Version + 1, Updated = timeProvider.GetUtcNowWithoutMicroseconds() };

        try
        {
            var replaceOneResult = await dbContext.ComplianceDeclarations.ReplaceOneAsync(
                session,
                filter,
                updated,
                new ReplaceOptions { IsUpsert = false },
                cancellationToken: cancellationToken
            );

            if (replaceOneResult.ModifiedCount == 0)
                throw new ConcurrencyException(
                    $"Concurrency issue on write, compliance declaration with id '{current.Id}' was not updated"
                );

            await auditEventService.RecordEvent(
                session,
                new AuditEventRequest(
                    Actor,
                    ComplianceDeclarationEntity,
                    AuditEventOperation.Update,
                    "submission.amended",
                    null,
                    updated.Id.ToString(),
                    updated.Version,
                    current.ToBsonDocument(),
                    updated.ToBsonDocument(),
                    updated.SchemaVersion,
                    updated.Updated,
                    ReadTraceId()
                ),
                cancellationToken
            );

            await session.CommitTransactionAsync(cancellationToken);
        }
        catch
        {
            await session.AbortTransactionAsync(CancellationToken.None);
            throw;
        }

        complianceDeclarationMetrics.Updated(updated.Status);
        logger.LogInformation("Updated compliance declaration with id '{ComplianceDeclarationId}'", updated.Id);

        return updated;
    }

    public Task<ComplianceDeclaration> UpdateStatus(
        ComplianceDeclaration current,
        ComplianceDeclarationStatus status,
        string? reason,
        User user,
        CancellationToken cancellationToken
    )
    {
        var updated = current.UpdateStatus(status, reason, user, timeProvider.GetUtcNowWithoutMicroseconds());

        return Update(current, updated, cancellationToken);
    }

    private string? ReadTraceId()
    {
        if (headerPropagationValues.Headers is null)
            return null;

        if (!headerPropagationValues.Headers.TryGetValue(traceHeaderOptions.Value.Name, out var values))
            return null;

        var traceId = values.ToString();

        return string.IsNullOrWhiteSpace(traceId) ? null : traceId;
    }
}
