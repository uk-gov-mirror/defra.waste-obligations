using System.Net;
using System.Net.Http.Json;
using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Api.Dtos;
using Defra.WasteObligations.Testing;
using Defra.WasteObligations.Testing.Fixtures.Entities;

namespace Defra.WasteObligations.Api.IntegrationTests.Scenarios;

public class SearchUnsubmittedComplianceDeclarationsTests : IntegrationTestBase
{
    [Fact]
    public async Task Search_WhenReady_ShouldReturnRegisteredResolvedOrganisationsWithoutActiveDeclaration()
    {
        var includedOrganisationId = Guid.NewGuid();
        var secondIncludedOrganisationId = Guid.NewGuid();
        var submittedOrganisationId = Guid.NewGuid();
        var generation = "generation";
        var verifiedAt = DateTime.UtcNow;
        await OrganisationEligibilitySnapshots.InsertOneAsync(
            new OrganisationEligibilitySnapshot
            {
                Id = OrganisationEligibilitySnapshot.SnapshotId,
                ActiveGeneration = generation,
                ActiveContentFingerprint = "fingerprint",
                ActiveRowCount = 4,
                ActiveGenerationPromotedAt = verifiedAt,
                LastVerifiedAt = verifiedAt,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        await OrganisationComplianceDeclarationEligibilities.InsertManyAsync(
            [
                Eligibility(includedOrganisationId, generation, "Alpha Packaging", "100001"),
                Eligibility(secondIncludedOrganisationId, generation, "Zeta Packaging", "100004") with
                {
                    RecyclingObligationsMet = true,
                    ObligationCoveragePercentage = 80,
                },
                Eligibility(submittedOrganisationId, generation, "Beta Packaging", "100002") with
                {
                    IsVisibleInUnsubmittedView = false,
                },
                Eligibility(Guid.NewGuid(), generation, "Cancelled Packaging", "100003") with
                {
                    RegistrationStatus = OrganisationRegistrationStatus.Cancelled,
                    IsVisibleInUnsubmittedView = false,
                },
            ],
            cancellationToken: TestContext.Current.CancellationToken
        );
        var client = CreateClient();

        var response = await client.GetAsync(
            Testing.Endpoints.ComplianceDeclarations.Unsubmitted(
                EndpointQuery
                    .New.Where(EndpointFilter.ObligationYear(2026))
                    .Where(EndpointFilter.RegistrationType("DirectProducer"))
                    .Where(EndpointFilter.Search("pack"))
                    .Where(EndpointFilter.Sort("Name[desc]"))
                    .Where(EndpointFilter.PageSize(1))
            ),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = await response.Content.ReadFromJsonAsync<UnsubmittedOrganisationsPaged>(
            TestContext.Current.CancellationToken
        );
        result.Should().NotBeNull();
        result.Total.Should().Be(2);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(1);
        result.UnsubmittedOrganisations.Should().ContainSingle();
        var row = result.UnsubmittedOrganisations.Single();
        row.OrganisationId.Should().Be(secondIncludedOrganisationId);
        row.ReferenceNumber.Should().Be("100004");
        row.ObligationCoveragePercentage.Should().Be(80);
        row.RecyclingObligationsMet.Should().BeTrue();
        await VerifyJson(responseBody);
    }

    [Fact]
    public async Task Search_WhenActiveGenerationIsMissing_ShouldReturnAnEmptyPage()
    {
        var client = CreateClient();

        var response = await client.GetAsync(
            Testing.Endpoints.ComplianceDeclarations.Unsubmitted(
                EndpointQuery
                    .New.Where(EndpointFilter.ObligationYear(2026))
                    .Where(EndpointFilter.RegistrationType("DirectProducer"))
            ),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<UnsubmittedOrganisationsPaged>(
            TestContext.Current.CancellationToken
        );

        result.Should().NotBeNull();
        result.Total.Should().Be(0);
        result.UnsubmittedOrganisations.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_WhenEligibilityGenerationIsStale_ShouldReturnItsLastActiveGeneration()
    {
        var organisationId = Guid.NewGuid();
        var verifiedAt = DateTime.UtcNow.AddHours(-3);
        await OrganisationEligibilitySnapshots.InsertOneAsync(
            new OrganisationEligibilitySnapshot
            {
                Id = OrganisationEligibilitySnapshot.SnapshotId,
                ActiveGeneration = "stale-generation",
                ActiveContentFingerprint = "fingerprint",
                ActiveRowCount = 1,
                ActiveGenerationPromotedAt = verifiedAt,
                LastVerifiedAt = verifiedAt,
            },
            cancellationToken: TestContext.Current.CancellationToken
        );
        await OrganisationComplianceDeclarationEligibilities.InsertOneAsync(
            Eligibility(organisationId, "stale-generation", "Alpha Packaging", "100001"),
            cancellationToken: TestContext.Current.CancellationToken
        );
        var client = CreateClient();

        var response = await client.GetAsync(
            Testing.Endpoints.ComplianceDeclarations.Unsubmitted(
                EndpointQuery
                    .New.Where(EndpointFilter.ObligationYear(2026))
                    .Where(EndpointFilter.RegistrationType("DirectProducer"))
            ),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<UnsubmittedOrganisationsPaged>(
            TestContext.Current.CancellationToken
        );

        result.Should().NotBeNull();
        result.Total.Should().Be(1);
        result.UnsubmittedOrganisations.Should().ContainSingle().Which.OrganisationId.Should().Be(organisationId);
    }

    private static OrganisationComplianceDeclarationEligibility Eligibility(
        Guid organisationId,
        string generation,
        string name,
        string referenceNumber
    ) =>
        OrganisationComplianceDeclarationEligibilityFixture
            .Default(organisationId)
            .With(x => x.Generation, generation)
            .With(x => x.Name, name)
            .With(x => x.ReferenceNumber, referenceNumber)
            .With(x => x.SourceFingerprint, name)
            .Create();
}
