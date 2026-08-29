using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.IntegrationTests.Infrastructure;
using Defra.WasteObligations.Api.Services;
using Defra.WasteObligations.Testing.Fixtures.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Defra.WasteObligations.Api.IntegrationTests.Services;

public class UnsubmittedEligibilityVisibilityServiceTests : IntegrationTestBase
{
    private const int ObligationYear = 2026;
    private const string ComplianceDeclarationOrganisationIndexName = "OrganisationId_ObligationYear";
    private const string EligibilityOrganisationKeyIndexName = "OrganisationId_ObligationYear_RegistrationType";

    [Fact]
    public async Task Apply_WhenCheckingRelevantDeclarations_ShouldUseTheOrganisationIndex()
    {
        var organisationId = Guid.NewGuid();
        await ComplianceDeclarations.InsertManyAsync(
            [
                ComplianceDeclarationFixture.DirectProducer(organisationId).Create(),
                .. Enumerable
                    .Range(0, 100)
                    .Select(_ => ComplianceDeclarationFixture.DirectProducer(Guid.NewGuid()).Create()),
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );
        var subject = CreateSubject();
        var row = Eligibility(organisationId);

        await using var profiler = await MongoQueryProfiler.Start(
            GetMongoDatabase(),
            [MongoQueryProfiler.IntegrationTestApplicationName],
            TestContext.Current.CancellationToken
        );
        var result = await subject.Apply([row], DateTime.UtcNow, TestContext.Current.CancellationToken);
        var profile = await profiler.Stop(TestContext.Current.CancellationToken);

        result.Single().IsVisibleInUnsubmittedView.Should().BeFalse();
        profile.QueriesWithoutAnIndex.Should().BeEmpty();
        profile
            .Queries.Should()
            .Contain(x =>
                x.Namespace == "waste-obligations.ComplianceDeclaration"
                && x.IndexNames.Contains(ComplianceDeclarationOrganisationIndexName)
            );
    }

    [Fact]
    public async Task Refresh_WhenUpdatingEligibilityRows_ShouldUseTheOrganisationKeyIndex()
    {
        var declaration = ComplianceDeclarationFixture.DirectProducer(Guid.NewGuid()).Create();
        await ComplianceDeclarations.InsertOneAsync(
            declaration,
            cancellationToken: TestContext.Current.CancellationToken
        );
        await OrganisationComplianceDeclarationEligibilities.InsertManyAsync(
            [
                Eligibility(declaration.Organisation.Id),
                .. Enumerable.Range(0, 100).Select(_ => Eligibility(Guid.NewGuid())),
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );
        var subject = CreateSubject();
        using var session = await GetMongoDatabase()
            .Client.StartSessionAsync(cancellationToken: TestContext.Current.CancellationToken);

        await using var profiler = await MongoQueryProfiler.Start(
            GetMongoDatabase(),
            [MongoQueryProfiler.IntegrationTestApplicationName],
            TestContext.Current.CancellationToken
        );
        await subject.Refresh(session, [declaration], DateTime.UtcNow, TestContext.Current.CancellationToken);
        var profile = await profiler.Stop(TestContext.Current.CancellationToken);

        profile.QueriesWithoutAnIndex.Should().BeEmpty();
        profile
            .Queries.Should()
            .Contain(x =>
                x.Namespace == "waste-obligations.OrganisationComplianceDeclarationEligibility"
                && x.IndexNames.Contains(EligibilityOrganisationKeyIndexName)
            );
    }

    private static UnsubmittedEligibilityVisibilityService CreateSubject()
    {
        var dbContext = new MongoDbContext(
            GetMongoDatabase(),
            Options.Create(new MongoDbOptions()),
            NullLogger<MongoDbContext>.Instance
        );

        return new UnsubmittedEligibilityVisibilityService(dbContext);
    }

    private static OrganisationComplianceDeclarationEligibility Eligibility(Guid organisationId) =>
        new()
        {
            Generation = "generation",
            OrganisationId = organisationId,
            ObligationYear = ObligationYear,
            RegistrationType = RegistrationType.DirectProducer,
            RegistrationStatus = OrganisationRegistrationStatus.Registered,
            Name = "Organisation",
            ReferenceNumber = "reference",
            ReferenceNumberResolutionState = OrganisationReferenceNumberResolutionState.Resolved,
            SourceFingerprint = "fingerprint",
            RefreshedAt = DateTime.UtcNow,
        };
}
