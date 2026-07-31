using AutoFixture;
using AwesomeAssertions;
using Defra.WasteObligations.Api.Data;
using Defra.WasteObligations.Api.Data.Entities;
using Defra.WasteObligations.Testing.Fixtures.Entities;
using ComplianceDeclaration = Defra.WasteObligations.Api.Data.Entities.ComplianceDeclaration;
using ComplianceDeclarationStatus = Defra.WasteObligations.Api.Data.Entities.ComplianceDeclarationStatus;
using Obligation = Defra.WasteObligations.Api.Data.Entities.Obligation;
using ReasonAuditEntry = Defra.WasteObligations.Api.Data.Entities.ReasonAuditEntry;
using UserLocale = Defra.WasteObligations.Api.Dtos.UserLocale;

namespace Defra.WasteObligations.Api.Tests.Data.Entities;

public class ComplianceDeclarationTests
{
    private DateTime UtcNow { get; } = new(2026, 5, 22, 16, 50, 0, DateTimeKind.Utc);

    [Fact]
    public void Submit_WhenNotUtcTimestamp_ShouldThrow()
    {
        var draft = CreateDraft();
        var user = UserFixture.Default().Create();

        var act = () => draft.Submit(user, DateTime.Now);

        act.Should().Throw<ArgumentException>().And.Message.Should().Be("Timestamp should be UTC");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("reason")]
    public void FromSubmittedToAccepted_ShouldBeAllowed(string? reason)
    {
        var draft = CreateDraft();
        var user = UserFixture.Default().With(x => x.Locale, UserLocale.Cy).Create();

        var submitted = draft.Submit(user, UtcNow);

        var accepted = submitted.UpdateStatus(
            ComplianceDeclarationStatus.Accepted,
            reason,
            user,
            UtcNow.AddSeconds(10)
        );

        accepted.Status.Should().Be(ComplianceDeclarationStatus.Accepted);
        accepted.Audit.Count().Should().Be(2);

        var audit = accepted.Audit.ToArray();
        audit[0].Action.Should().Be(nameof(ComplianceDeclarationStatus.Submitted));
        audit[0].User.Should().Be(user);
        audit[0].User.Locale.Should().Be(UserLocale.Cy);
        audit[1].Action.Should().Be(nameof(ComplianceDeclarationStatus.Accepted));
        audit[1].User.Should().Be(user);
        audit[1].Timestamp.Should().BeAfter(audit[0].Timestamp);

        if (reason is not null)
        {
            var reasonAudit = audit[1] as ReasonAuditEntry;
            reasonAudit.Should().NotBeNull();
            reasonAudit.Reason.Should().Be(reason);
        }
    }

    [Fact]
    public void FromAcceptedToCancelled_ShouldBeAllowed()
    {
        const string reason = "Cancellation reason";
        var accepted = CreateDraft() with { Status = ComplianceDeclarationStatus.Accepted };
        var user = UserFixture.Default().Create();

        var cancelled = accepted.UpdateStatus(
            ComplianceDeclarationStatus.Cancelled,
            reason,
            user,
            UtcNow.AddSeconds(10)
        );

        cancelled.Status.Should().Be(ComplianceDeclarationStatus.Cancelled);
        var audit = cancelled.Audit.Should().ContainSingle().Which;
        audit.Action.Should().Be(nameof(ComplianceDeclarationStatus.Cancelled));
        audit.User.Should().Be(user);
        audit.Timestamp.Should().Be(UtcNow.AddSeconds(10));
        audit.Should().BeOfType<ReasonAuditEntry>().Which.Reason.Should().Be(reason);
    }

    [Theory]
    [InlineData(ComplianceDeclarationStatus.Submitted, ComplianceDeclarationStatus.Submitted)]
    [InlineData(ComplianceDeclarationStatus.Cancelled, ComplianceDeclarationStatus.Accepted)]
    [InlineData(ComplianceDeclarationStatus.Accepted, ComplianceDeclarationStatus.Submitted)]
    [InlineData(ComplianceDeclarationStatus.Cancelled, ComplianceDeclarationStatus.Submitted)]
    public void FromStatusToStatus_ShouldNotBeAllowed(
        ComplianceDeclarationStatus startStatus,
        ComplianceDeclarationStatus nextStatus
    )
    {
        var draft = CreateDraft() with { Status = startStatus };
        var user = UserFixture.Default().Create();

        var act = () => draft.UpdateStatus(nextStatus, null, user, UtcNow.AddSeconds(10));

        act.Should().Throw<EntityException>();
    }

    [Fact]
    public void Submit_WhenObligationsProvided_ShouldCalculateObligationCoveragePercentage()
    {
        var draft = CreateDraft(
            ObligationFixture
                .Default()
                .With(
                    x => x.Tonnages,
                    ObligationTonnagesFixture.Default().With(t => t.Accepted, 2).With(t => t.Obligated, 5).Create()
                )
                .Create()
        );
        var user = UserFixture.Default().Create();

        var submitted = draft.Submit(user, UtcNow);

        submitted.ObligationCoveragePercentage.Should().Be(40m);
    }

    [Fact]
    public void Submit_WhenRepeatingDecimal_ShouldRoundToWholeNumber()
    {
        var draft = CreateDraft(
            ObligationFixture
                .Default()
                .With(
                    x => x.Tonnages,
                    ObligationTonnagesFixture.Default().With(t => t.Accepted, 1).With(t => t.Obligated, 3).Create()
                )
                .Create()
        );
        var user = UserFixture.Default().Create();

        var submitted = draft.Submit(user, UtcNow);

        submitted.ObligationCoveragePercentage.Should().Be(33m);
    }

    [Fact]
    public void Submit_WhenTotalObligatedIsZero_ShouldSetObligationCoveragePercentageToZero()
    {
        var draft = CreateDraft(
            ObligationFixture
                .Default()
                .With(x => x.Tonnages, ObligationTonnagesFixture.Default().With(t => t.Obligated, 0).Create())
                .Create()
        );
        var user = UserFixture.Default().Create();

        var submitted = draft.Submit(user, UtcNow);

        submitted.ObligationCoveragePercentage.Should().Be(0m);
    }

    [Fact]
    public void Submit_WhenMidpointPercentage_ShouldRoundAwayFromZero()
    {
        var draft = CreateDraft(
            ObligationFixture
                .Default()
                .With(
                    x => x.Tonnages,
                    ObligationTonnagesFixture.Default().With(t => t.Accepted, 1).With(t => t.Obligated, 200).Create()
                )
                .Create()
        );
        var user = UserFixture.Default().Create();

        var submitted = draft.Submit(user, UtcNow);

        submitted.ObligationCoveragePercentage.Should().Be(1m);
    }

    private static ComplianceDeclaration CreateDraft() =>
        new()
        {
            Organisation = OrganisationFixture.Organisation().Create(),
            ObligationStatus = "Met",
            SubmitterName = "Submitter",
        };

    private static ComplianceDeclaration CreateDraft(Obligation obligation) =>
        CreateDraft() with
        {
            Obligations = [obligation],
        };
}
