using System.Net;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Services;
using Defra.WasteObligations.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using EntityRegistrationType = Defra.WasteObligations.Api.Data.Entities.RegistrationType;

namespace Defra.WasteObligations.Api.Tests.Endpoints.ComplianceDeclarations;

public class SearchUnsubmittedComplianceDeclarationsTests(
    ApiWebApplicationFactory factory,
    ITestOutputHelper outputHelper
) : EndpointTestBase(factory, outputHelper)
{
    private IUnsubmittedOrganisationsService UnsubmittedOrganisationsService { get; } =
        Substitute.For<IUnsubmittedOrganisationsService>();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.AddTransient<IUnsubmittedOrganisationsService>(_ => UnsubmittedOrganisationsService);
    }

    [Fact]
    public async Task WhenValid_ShouldMapRequestAndReturnUnsubmittedDeclarations()
    {
        var organisationId = Guid.Parse("c961459a-324c-4400-bb22-afae8c8a9827");
        UnsubmittedOrganisationsService
            .Search(
                2026,
                Arg.Is<IReadOnlyCollection<EntityRegistrationType>?>(x =>
                    x != null && x.Count == 1 && x.Single() == EntityRegistrationType.DirectProducer
                ),
                "alpha",
                Arg.Is<IReadOnlyCollection<UnsubmittedOrganisationSort>?>(x =>
                    x != null
                    && x.Count == 2
                    && x.ElementAt(0).Field == UnsubmittedOrganisationSortField.OrganisationName
                    && x.ElementAt(0).Direction == UnsubmittedOrganisationSortDirection.Descending
                    && x.ElementAt(1).Field == UnsubmittedOrganisationSortField.PercentageMet
                    && x.ElementAt(1).Direction == UnsubmittedOrganisationSortDirection.Ascending
                ),
                page: 2,
                pageSize: 5,
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(
                new UnsubmittedOrganisationSearchResult
                {
                    Rows =
                    [
                        new UnsubmittedOrganisationSearchRow
                        {
                            OrganisationId = organisationId,
                            ObligationYear = 2026,
                            RegistrationType = EntityRegistrationType.DirectProducer,
                            Name = "Alpha Packaging",
                            ReferenceNumber = "100001",
                            RecyclingObligationsMet = true,
                            ObligationCoveragePercentage = 80,
                        },
                    ],
                    Total = 6,
                }
            );
        var client = CreateClient(testUser: TestUser.ReadOnly);

        var response = await client.GetAsync(
            Testing.Endpoints.ComplianceDeclarations.Unsubmitted(
                EndpointQuery
                    .New.Where(EndpointFilter.ObligationYear(2026))
                    .Where(EndpointFilter.RegistrationType("DirectProducer"))
                    .Where(EndpointFilter.Search("alpha"))
                    .Where(EndpointFilter.Sort("OrganisationName[desc],PercentageMet[asc]"))
                    .Where(EndpointFilter.Page(2))
                    .Where(EndpointFilter.PageSize(5))
            ),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await VerifyJson(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenValidForComplianceScheme_ShouldUseDefaultsAndReturnResponseShape()
    {
        var organisationId = Guid.Parse("b4d5584e-fa55-431f-9fcc-1bf747e001e4");
        UnsubmittedOrganisationsService
            .Search(
                2026,
                Arg.Is<IReadOnlyCollection<EntityRegistrationType>?>(x =>
                    x != null && x.Count == 1 && x.Single() == EntityRegistrationType.ComplianceScheme
                ),
                null,
                Arg.Is<IReadOnlyCollection<UnsubmittedOrganisationSort>?>(x => x != null && x.Count == 0),
                page: 1,
                pageSize: 20,
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(
                new UnsubmittedOrganisationSearchResult
                {
                    Rows =
                    [
                        new UnsubmittedOrganisationSearchRow
                        {
                            OrganisationId = organisationId,
                            ObligationYear = 2026,
                            RegistrationType = EntityRegistrationType.ComplianceScheme,
                            Name = "Bravo Scheme",
                            ReferenceNumber = "200001",
                        },
                    ],
                    Total = 1,
                }
            );
        var client = CreateClient(testUser: TestUser.ReadOnly);

        var response = await client.GetAsync(
            Testing.Endpoints.ComplianceDeclarations.Unsubmitted(
                EndpointQuery
                    .New.Where(EndpointFilter.ObligationYear(2026))
                    .Where(EndpointFilter.RegistrationType("ComplianceScheme"))
            ),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await VerifyJson(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenYearAndRegistrationTypeAreOmitted_ShouldSearchAcrossAllVisibleRows()
    {
        var organisationId = Guid.Parse("d14ce75d-cd19-4b7b-a572-3df93758e59d");
        UnsubmittedOrganisationsService
            .Search(
                null,
                Arg.Is<IReadOnlyCollection<EntityRegistrationType>?>(x => x != null && x.Count == 0),
                null,
                Arg.Is<IReadOnlyCollection<UnsubmittedOrganisationSort>?>(x => x != null && x.Count == 0),
                page: 1,
                pageSize: 20,
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(
                new UnsubmittedOrganisationSearchResult
                {
                    Rows =
                    [
                        new UnsubmittedOrganisationSearchRow
                        {
                            OrganisationId = organisationId,
                            ObligationYear = 2025,
                            RegistrationType = EntityRegistrationType.ComplianceScheme,
                            Name = "Historic Scheme",
                            ReferenceNumber = "200002",
                        },
                    ],
                    Total = 1,
                }
            );
        var client = CreateClient(testUser: TestUser.ReadOnly);

        var response = await client.GetAsync(
            Testing.Endpoints.ComplianceDeclarations.Unsubmitted(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await VerifyJson(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenMultipleRegistrationTypesAreProvided_ShouldPassThemToTheService()
    {
        UnsubmittedOrganisationsService
            .Search(
                2026,
                Arg.Is<IReadOnlyCollection<EntityRegistrationType>?>(x =>
                    x != null
                    && x.Count == 2
                    && x.ElementAt(0) == EntityRegistrationType.DirectProducer
                    && x.ElementAt(1) == EntityRegistrationType.ComplianceScheme
                ),
                null,
                Arg.Any<IReadOnlyCollection<UnsubmittedOrganisationSort>?>(),
                page: 1,
                pageSize: 20,
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(new UnsubmittedOrganisationSearchResult { Rows = [], Total = 0 });
        var client = CreateClient(testUser: TestUser.ReadOnly);

        var response = await client.GetAsync(
            Testing.Endpoints.ComplianceDeclarations.Unsubmitted(
                EndpointQuery
                    .New.Where(EndpointFilter.ObligationYear(2026))
                    .Where(EndpointFilter.RegistrationType("DirectProducer,ComplianceScheme"))
            ),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WhenPercentageMetSortIsValidForDirectProducer_ShouldPassTheDedicatedSortToTheService()
    {
        UnsubmittedOrganisationsService
            .Search(
                2026,
                Arg.Is<IReadOnlyCollection<EntityRegistrationType>?>(x =>
                    x != null && x.Count == 1 && x.Single() == EntityRegistrationType.DirectProducer
                ),
                null,
                Arg.Is<IReadOnlyCollection<UnsubmittedOrganisationSort>?>(x =>
                    x != null
                    && x.Count == 1
                    && x.Single().Field == UnsubmittedOrganisationSortField.PercentageMet
                    && x.Single().Direction == UnsubmittedOrganisationSortDirection.Ascending
                ),
                page: 1,
                pageSize: 20,
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(new UnsubmittedOrganisationSearchResult { Rows = [], Total = 0 });
        var client = CreateClient(testUser: TestUser.ReadOnly);

        var response = await client.GetAsync(
            Testing.Endpoints.ComplianceDeclarations.Unsubmitted(
                EndpointQuery
                    .New.Where(EndpointFilter.ObligationYear(2026))
                    .Where(EndpointFilter.RegistrationType("DirectProducer"))
                    .Where(EndpointFilter.Sort("PercentageMet[asc]"))
            ),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WhenWriteOnlyUser_ShouldBeForbidden()
    {
        var client = CreateClient(testUser: TestUser.WriteOnly);

        var response = await client.GetAsync(
            Testing.Endpoints.ComplianceDeclarations.Unsubmitted(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(2022)]
    [InlineData(2051)]
    public async Task Validation_WhenObligationYearInvalid_ShouldBeBadRequest(int obligationYear)
    {
        var content = await RequestShouldBeBadRequest(
            EndpointQuery
                .New.Where(EndpointFilter.ObligationYear(obligationYear))
                .Where(EndpointFilter.RegistrationType("DirectProducer"))
        );

        await VerifyJson(content);
    }

    [Fact]
    public async Task Validation_WhenRegistrationTypeUnknown_ShouldBeBadRequest()
    {
        var content = await RequestShouldBeBadRequest(
            EndpointQuery
                .New.Where(EndpointFilter.ObligationYear(2026))
                .Where(EndpointFilter.RegistrationType("Unknown"))
        );

        await VerifyJson(content);
    }

    [Fact]
    public async Task Validation_WhenSearchIsTooLong_ShouldBeBadRequest()
    {
        var content = await RequestShouldBeBadRequest(
            EndpointQuery
                .New.Where(EndpointFilter.ObligationYear(2026))
                .Where(EndpointFilter.RegistrationType("DirectProducer"))
                .Where(EndpointFilter.Search(new string('a', 101)))
        );

        await VerifyJson(content);
    }

    [Theory]
    [InlineData("Unknown[asc]")]
    [InlineData("OrganisationName[ascending]")]
    [InlineData("OrganisationName[asc],OrganisationName[desc]")]
    public async Task Validation_WhenSortInvalid_ShouldBeBadRequest(string sort)
    {
        var content = await RequestShouldBeBadRequest(
            EndpointQuery
                .New.Where(EndpointFilter.ObligationYear(2026))
                .Where(EndpointFilter.RegistrationType("DirectProducer"))
                .Where(EndpointFilter.Sort(sort))
        );

        await VerifyJson(content);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Validation_WhenPageSizeInvalid_ShouldBeBadRequest(int pageSize)
    {
        var content = await RequestShouldBeBadRequest(
            EndpointQuery
                .New.Where(EndpointFilter.ObligationYear(2026))
                .Where(EndpointFilter.RegistrationType("DirectProducer"))
                .Where(EndpointFilter.PageSize(pageSize))
        );

        await VerifyJson(content);
    }

    [Fact]
    public async Task WhenPercentageMetSortIsRequestedByComplianceScheme_ShouldPassTheDedicatedSortToTheService()
    {
        UnsubmittedOrganisationsService
            .Search(
                2026,
                Arg.Is<IReadOnlyCollection<EntityRegistrationType>?>(x =>
                    x != null && x.Count == 1 && x.Single() == EntityRegistrationType.ComplianceScheme
                ),
                null,
                Arg.Is<IReadOnlyCollection<UnsubmittedOrganisationSort>?>(x =>
                    x != null
                    && x.Count == 1
                    && x.Single().Field == UnsubmittedOrganisationSortField.PercentageMet
                    && x.Single().Direction == UnsubmittedOrganisationSortDirection.Ascending
                ),
                page: 1,
                pageSize: 20,
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(new UnsubmittedOrganisationSearchResult { Rows = [], Total = 0 });
        var client = CreateClient(testUser: TestUser.ReadOnly);

        var response = await client.GetAsync(
            Testing.Endpoints.ComplianceDeclarations.Unsubmitted(
                EndpointQuery
                    .New.Where(EndpointFilter.ObligationYear(2026))
                    .Where(EndpointFilter.RegistrationType("ComplianceScheme"))
                    .Where(EndpointFilter.Sort("PercentageMet[asc]"))
            ),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await UnsubmittedOrganisationsService
            .Received(1)
            .Search(
                2026,
                Arg.Is<IReadOnlyCollection<EntityRegistrationType>?>(x =>
                    x != null && x.Count == 1 && x.Single() == EntityRegistrationType.ComplianceScheme
                ),
                null,
                Arg.Is<IReadOnlyCollection<UnsubmittedOrganisationSort>?>(x =>
                    x != null
                    && x.Count == 1
                    && x.Single().Field == UnsubmittedOrganisationSortField.PercentageMet
                    && x.Single().Direction == UnsubmittedOrganisationSortDirection.Ascending
                ),
                1,
                20,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task WhenObligationYearIsHistoric_ShouldSearchAndReturnResponse()
    {
        const int obligationYear = 2025;
        var organisationId = Guid.Parse("c3321717-35ee-4016-a63d-9a0b7c5b27f9");
        UnsubmittedOrganisationsService
            .Search(
                obligationYear,
                Arg.Is<IReadOnlyCollection<EntityRegistrationType>?>(x =>
                    x != null && x.Count == 1 && x.Single() == EntityRegistrationType.DirectProducer
                ),
                null,
                Arg.Is<IReadOnlyCollection<UnsubmittedOrganisationSort>?>(x => x != null && x.Count == 0),
                page: 1,
                pageSize: 20,
                cancellationToken: Arg.Any<CancellationToken>()
            )
            .Returns(
                new UnsubmittedOrganisationSearchResult
                {
                    Rows =
                    [
                        new UnsubmittedOrganisationSearchRow
                        {
                            OrganisationId = organisationId,
                            ObligationYear = obligationYear,
                            RegistrationType = EntityRegistrationType.DirectProducer,
                            Name = "Historic Packaging",
                            ReferenceNumber = "100003",
                        },
                    ],
                    Total = 1,
                }
            );
        var client = CreateClient(testUser: TestUser.ReadOnly);

        var response = await client.GetAsync(
            Testing.Endpoints.ComplianceDeclarations.Unsubmitted(
                EndpointQuery
                    .New.Where(EndpointFilter.ObligationYear(obligationYear))
                    .Where(EndpointFilter.RegistrationType("DirectProducer"))
            ),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await VerifyJson(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await UnsubmittedOrganisationsService
            .Received(1)
            .Search(
                obligationYear,
                Arg.Is<IReadOnlyCollection<EntityRegistrationType>?>(x =>
                    x != null && x.Count == 1 && x.Single() == EntityRegistrationType.DirectProducer
                ),
                null,
                Arg.Is<IReadOnlyCollection<UnsubmittedOrganisationSort>?>(x => x != null && x.Count == 0),
                page: 1,
                pageSize: 20,
                Arg.Any<CancellationToken>()
            );
    }

    private async Task<string> RequestShouldBeBadRequest(EndpointQuery query)
    {
        var client = CreateClient(testUser: TestUser.ReadOnly);

        var response = await client.GetAsync(
            Testing.Endpoints.ComplianceDeclarations.Unsubmitted(query),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
