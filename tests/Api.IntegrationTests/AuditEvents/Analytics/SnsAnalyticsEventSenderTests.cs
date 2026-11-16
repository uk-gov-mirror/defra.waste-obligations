using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Testing.Authentication;
using Defra.WasteObligations.Testing.Extensions.WireMock;
using Defra.WasteObligations.Testing.Fixtures.Dtos;
using Defra.WasteObligations.Testing.Fixtures.Entities;
using MongoDB.Bson;
using ComplianceDeclaration = Defra.WasteObligations.Api.Dtos.ComplianceDeclaration;
using ComplianceDeclarationEntity = Defra.WasteObligations.Api.Data.Entities.ComplianceDeclaration;

namespace Defra.WasteObligations.Api.IntegrationTests.AuditEvents.Analytics;

public class SnsAnalyticsEventSenderTests : IntegrationTestBase
{
    [Theory]
    [InlineData("insert", "create", "obligation_calculation.created", 1)]
    [InlineData("update", "update", "obligation_calculation.updated", 2)]
    public async Task WhenCalculationEventRecorded_ShouldPublishSchemaBoundMessage(
        string outboxOperation,
        string operation,
        string eventType,
        int version
    )
    {
        var previous = ObligationCalculationFixture.Default().Create();
        var calculation = previous with
        {
            Version = version,
            CalculatedOn = previous.CalculatedOn.AddSeconds(1),
            Producers = previous
                .Producers.Select(x =>
                    x with
                    {
                        Materials = x
                            .Materials.Select(material =>
                                material.MaterialCode == "PL" ? material with { ObligationTonnes = 29 } : material
                            )
                            .ToArray(),
                    }
                )
                .ToArray(),
        };
        var after = calculation.ToBsonDocument();
        after["notInSchema"] = "must not be published";
        var auditEvent = AuditEventFixture
            .ObligationCalculation(Guid.NewGuid().ToString(), version)
            .With(x => x.EntityId, calculation.Id.ToString())
            .With(x => x.Operation, outboxOperation)
            .With(x => x.EventType, eventType)
            .With(x => x.Version, version)
            .With(x => x.OccurredAt, calculation.CalculatedOn)
            .With(x => x.RecordedAt, calculation.CalculatedOn.AddMilliseconds(123))
            .With(x => x.TraceId, TraceId)
            .With(x => x.Before, operation == "create" ? null : previous.ToBsonDocument())
            .With(x => x.After, after)
            .Create();
        using var sqsClient = CreateSqsClient();

        await AuditEvents.InsertOneAsync(auditEvent, cancellationToken: TestContext.Current.CancellationToken);
        using var message = await ReceiveAnalyticsEventsQueueJsonMessage(
            sqsClient,
            root => root.GetProperty("eventId").GetString() == auditEvent.EventId
        );
        var root = message.RootElement;

        root.GetProperty("eventId").GetString().Should().Be(auditEvent.EventId);
        root.GetProperty("sequence").GetInt64().Should().Be(version);
        root.GetProperty("entity").GetString().Should().Be("obligation_calculation");
        root.GetProperty("entityId").GetString().Should().Be($"oblcalc_{calculation.Id}");
        root.GetProperty("operation").GetString().Should().Be(operation);
        root.GetProperty("eventType").GetString().Should().Be(eventType);
        root.GetProperty("schemaVersion").GetString().Should().Be("obligation_calculation_v1.0");
        root.GetProperty("actor").GetString().Should().Be("system:waste-obligations");
        root.GetProperty("correlationId").GetString().Should().Be(TraceId);
        root.GetProperty("piiKeyRef").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("deletedReason").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("occurredAt").GetString().Should().Be("2026-01-02T03:04:06.000Z");
        root.GetProperty("recordedAt").GetString().Should().Be("2026-01-02T03:04:06.123Z");
        root.GetProperty("version").GetInt32().Should().Be(version);
        AssertCalculationPayload(root.GetProperty("after"), calculation, 29);
        if (operation == "create")
            root.GetProperty("before").ValueKind.Should().Be(JsonValueKind.Null);
        else
            AssertCalculationPayload(root.GetProperty("before"), previous, 28);
    }

    [Fact]
    public async Task WhenAuditEventCreated_ShouldPublishJsonToSubscribedQueue()
    {
        using var sqsClient = CreateSqsClient();
        var client = CreateClient();

        var complianceDeclaration = await CreateComplianceDeclaration(client);
        using var deserializedMessage = await ReceiveAnalyticsEventsQueueJsonMessage(
            sqsClient,
            MatchAnalyticsEvent(complianceDeclaration.Id, "create", "submission.created")
        );
        var root = deserializedMessage.RootElement;

        root.GetProperty("eventId").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("entityId").GetString().Should().Be($"cdec_{complianceDeclaration.Id}");
        root.GetProperty("operation").GetString().Should().Be("create");
        root.GetProperty("eventType").GetString().Should().Be("submission.created");
        root.GetProperty("actor").GetString().Should().Be("user:e72be574-8b5b-4836-af47-dd7e0c0d1d87");
        root.GetProperty("deletedReason").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("piiKeyRef").ValueKind.Should().Be(JsonValueKind.Null);
        root.TryGetProperty("correlationId", out _).Should().BeFalse();
        root.GetProperty("schemaVersion")
            .GetString()
            .Should()
            .Be($"compliance_declaration_{ComplianceDeclarationEntity.SchemaVersionValue}");
        root.GetProperty("before").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("after").GetProperty("id").GetString().Should().Be(complianceDeclaration.Id);
    }

    [Fact]
    public async Task WhenAuditEventUpdated_ShouldPublishJsonToSubscribedQueue()
    {
        using var sqsClient = CreateSqsClient();
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TraceHeaderName, TraceId);
        var complianceDeclaration = await CreateComplianceDeclaration(client);
        await ReceiveAnalyticsEventsQueueJsonMessage(
            sqsClient,
            MatchAnalyticsEvent(complianceDeclaration.Id, "create", "submission.created")
        );

        var response = await client.PatchAsJsonAsync(
            Testing.Endpoints.Organisations.ComplianceDeclarations.Update(
                complianceDeclaration.Organisation.Id,
                complianceDeclaration.Id
            ),
            UpdateComplianceDeclarationRequestFixture.Accepted().Create(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var deserializedMessage = await ReceiveAnalyticsEventsQueueJsonMessage(
            sqsClient,
            MatchAnalyticsEvent(complianceDeclaration.Id, "update", "submission.amended")
        );
        var root = deserializedMessage.RootElement;

        root.GetProperty("entityId").GetString().Should().Be($"cdec_{complianceDeclaration.Id}");
        root.GetProperty("operation").GetString().Should().Be("update");
        root.GetProperty("eventType").GetString().Should().Be("submission.amended");
        root.GetProperty("actor").GetString().Should().Be("user:7e91f2ac-5b44-4c8d-ae73-1d9f62b8e0f4");
        root.GetProperty("deletedReason").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("piiKeyRef").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("correlationId").GetString().Should().Be(TraceId);
        root.GetProperty("version").GetInt32().Should().Be(2);
        root.GetProperty("before").GetProperty("status").GetString().Should().Be("Submitted");
        root.GetProperty("after").GetProperty("status").GetString().Should().Be("Accepted");
    }

    [Fact]
    public async Task WhenAuditEventDeleted_ShouldPublishJsonToSubscribedQueue()
    {
        using var sqsClient = CreateSqsClient();
        var client = CreateClient();
        var complianceDeclaration = await CreateComplianceDeclaration(client);
        await ReceiveAnalyticsEventsQueueJsonMessage(
            sqsClient,
            MatchAnalyticsEvent(complianceDeclaration.Id, "create", "submission.created")
        );

        var response = await client.DeleteAsync(
            Testing.Endpoints.ComplianceDeclarations.Delete(complianceDeclaration.Id),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var deserializedMessage = await ReceiveAnalyticsEventsQueueJsonMessage(
            sqsClient,
            MatchAnalyticsEvent(complianceDeclaration.Id, "delete", "submission.removed")
        );
        var root = deserializedMessage.RootElement;

        root.GetProperty("entityId").GetString().Should().Be($"cdec_{complianceDeclaration.Id}");
        root.GetProperty("operation").GetString().Should().Be("delete");
        root.GetProperty("eventType").GetString().Should().Be("submission.removed");
        root.GetProperty("actor").GetString().Should().Be("service:waste-obligations");
        root.GetProperty("deletedReason").GetString().Should().Be("elevated_system_allowed_removal");
        root.GetProperty("piiKeyRef").ValueKind.Should().Be(JsonValueKind.Null);
        root.TryGetProperty("correlationId", out _).Should().BeFalse();
        root.GetProperty("version").GetInt32().Should().Be(2);
        root.TryGetProperty("traceId", out _).Should().BeFalse();
        root.GetProperty("before").GetProperty("id").GetString().Should().Be(complianceDeclaration.Id);
        root.GetProperty("after").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static void AssertCalculationPayload(
        JsonElement payload,
        ObligationCalculation calculation,
        int plasticObligationTonnes
    )
    {
        payload.GetProperty("id").GetString().Should().Be(calculation.Id.ToString());
        payload.GetProperty("version").GetInt32().Should().Be(calculation.Version);
        payload.GetProperty("schemaVersion").GetString().Should().Be("v1.0");
        payload.GetProperty("submitterOrganisationId").GetGuid().Should().Be(calculation.SubmitterOrganisationId);
        payload.GetProperty("submitterType").GetString().Should().Be("DirectRegistrant");
        payload.GetProperty("obligationYear").GetInt32().Should().Be(2025);
        payload.GetProperty("calculatedOn").GetDateTimeOffset().Should().Be(calculation.CalculatedOn);
        payload.TryGetProperty("notInSchema", out _).Should().BeFalse();
        payload.TryGetProperty("_id", out _).Should().BeFalse();
        var producers = payload.GetProperty("producers");
        producers.GetArrayLength().Should().Be(2);
        for (var index = 0; index < producers.GetArrayLength(); index++)
        {
            producers[index]
                .GetProperty("producerOrganisationId")
                .GetGuid()
                .Should()
                .Be(calculation.Producers[index].ProducerOrganisationId);
            var materials = producers[index].GetProperty("materials");
            materials.GetArrayLength().Should().Be(index == 0 ? 3 : 1);
            materials[0].GetProperty("materialCode").GetString().Should().Be("PL");
            materials[0].GetProperty("inputKilograms").GetDecimal().Should().Be(50_100);
            materials[0].GetProperty("inputTonnes").GetDecimal().Should().Be(50);
            materials[0].GetProperty("obligationTonnes").GetDecimal().Should().Be(plasticObligationTonnes);
        }
        var glass = producers[0].GetProperty("materials");
        glass[1].GetProperty("materialCode").GetString().Should().Be("GL");
        glass[1].GetProperty("inputKilograms").GetDecimal().Should().Be(3_000);
        glass[1].GetProperty("inputTonnes").GetDecimal().Should().Be(3);
        glass[1].GetProperty("obligationTonnes").GetDecimal().Should().Be(1);
        glass[2].GetProperty("materialCode").GetString().Should().Be("GR");
        glass[2].GetProperty("inputKilograms").GetDecimal().Should().Be(3_000);
        glass[2].GetProperty("inputTonnes").GetDecimal().Should().Be(3);
        glass[2].GetProperty("obligationTonnes").GetDecimal().Should().Be(2);
    }

    private async Task<ComplianceDeclaration> CreateComplianceDeclaration(HttpClient client)
    {
        var organisationId = Guid.NewGuid();
        await WireMockContext.WireMockAdminApi.StubWasteOrganisationsOrganisationRequest(
            organisationId,
            BasicAuthCredential.ForClient(ClientIds.WasteOrganisations)
        );
        var response = await client.PostAsJsonAsync(
            Testing.Endpoints.Organisations.ComplianceDeclarations.Create(organisationId),
            CreateComplianceDeclarationRequestFixture.DirectProducer(organisationId).Create(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var complianceDeclaration = await response.Content.ReadFromJsonAsync<ComplianceDeclaration>(
            TestContext.Current.CancellationToken
        );

        complianceDeclaration.Should().NotBeNull();

        return complianceDeclaration;
    }
}
