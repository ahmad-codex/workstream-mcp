using System;
using System.Collections.Generic;
using FluentAssertions;
using Workstream.Core.Domain;
using Workstream.Core.Errors;
using Workstream.Core.StateMachine;
using Workstream.UnitTests.Fixtures;
using Xunit;

namespace Workstream.UnitTests;

public sealed class StateMachineServiceTests : IClassFixture<PlanProfileFixture>
{
    private readonly PlanProfileFixture _fx;
    private readonly StateMachineService _svc;

    public StateMachineServiceTests(PlanProfileFixture fx)
    {
        _fx = fx;
        _svc = new StateMachineService(fx.Cache);
    }

    // -----------------------------------------------------------------------
    // Audit profile — legal happy-path transitions
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("pending",     "claimed",     "claim_task",     "auditor")]
    [InlineData("claimed",     "in_progress", "start_work",     null)]
    [InlineData("claimed",     "pending",     "release_claim",  null)]
    public void Audit_TaskTransitions_LegalSimpleCases(string from, string to, string via, string? role)
    {
        var r = _svc.ValidateTransition(_fx.Audit, EntityType.Task, from, to, via,
            HumanActor(), role, TransitionContext.Empty);
        r.Allowed.Should().BeTrue($"because {from}->{to} via {via} (role={role}) is legal on audit");
    }

    [Fact]
    public void Audit_SubmitFindings_Empty_GoesDirectlyToDone()
    {
        var r = _svc.ValidateTransition(_fx.Audit, EntityType.Task, "in_progress", "done", "submit_findings",
            HumanActor(), "auditor",
            new TransitionContext(ProposedFindingCount: 0));
        r.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Audit_SubmitFindings_NonEmpty_GoesToReview()
    {
        var r = _svc.ValidateTransition(_fx.Audit, EntityType.Task, "in_progress", "review", "submit_findings",
            HumanActor(), "auditor",
            new TransitionContext(ProposedFindingCount: 2));
        r.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Audit_SubmitFindings_NonEmpty_CannotGoToDone()
    {
        var r = _svc.ValidateTransition(_fx.Audit, EntityType.Task, "in_progress", "done", "submit_findings",
            HumanActor(), "auditor",
            new TransitionContext(ProposedFindingCount: 2));
        r.Allowed.Should().BeFalse();
        r.RejectionCode.Should().Be(ErrorCodes.GuardFailed);
    }

    [Fact]
    public void Audit_SubmitFindings_Empty_CannotGoToReview()
    {
        var r = _svc.ValidateTransition(_fx.Audit, EntityType.Task, "in_progress", "review", "submit_findings",
            HumanActor(), "auditor",
            new TransitionContext(ProposedFindingCount: 0));
        r.Allowed.Should().BeFalse();
        r.RejectionCode.Should().Be(ErrorCodes.GuardFailed);
    }

    // -----------------------------------------------------------------------
    // Audit profile — finding transitions
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("pending_verification", "confirmed",  "submit_verification_verdict", "verifier", "confirmed")]
    [InlineData("pending_verification", "rejected",   "submit_verification_verdict", "verifier", "rejected")]
    [InlineData("pending_verification", "ambiguous",  "submit_verification_verdict", "verifier", "ambiguous")]
    [InlineData("confirmed",            "in_fix",     "claim_fix",                   "fixer",    null)]
    [InlineData("in_fix",               "fixed",      "submit_attempt_verdict",      "fix_verifier", "fix_confirmed")]
    public void Audit_FindingTransitions_LegalRoleAndVerdict(string from, string to, string via, string role, string? verdict)
    {
        var r = _svc.ValidateTransition(_fx.Audit, EntityType.Finding, from, to, via,
            HumanActor(), role,
            new TransitionContext(Verdict: verdict));
        r.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Audit_FixFailed_BelowCap_GoesBackToInFix()
    {
        var r = _svc.ValidateTransition(_fx.Audit, EntityType.Finding, "fix_failed", "in_fix", "claim_fix",
            HumanActor(), "fixer",
            new TransitionContext(AttemptCount: 1, RetryCap: 3));
        r.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Audit_FixFailed_AtCap_EscalatesToHumanReview()
    {
        var r = _svc.ValidateTransition(_fx.Audit, EntityType.Finding, "fix_failed", "needs_human_review", "escalate",
            HumanActor(), null,
            new TransitionContext(AttemptCount: 3, RetryCap: 3));
        r.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Audit_FixFailed_AtCap_CannotGoBackToInFix()
    {
        var r = _svc.ValidateTransition(_fx.Audit, EntityType.Finding, "fix_failed", "in_fix", "claim_fix",
            HumanActor(), "fixer",
            new TransitionContext(AttemptCount: 3, RetryCap: 3));
        r.Allowed.Should().BeFalse();
        r.RejectionCode.Should().Be(ErrorCodes.GuardFailed);
    }

    // -----------------------------------------------------------------------
    // Audit profile — illegal transitions
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("pending",     "in_progress", "claim_task")]
    [InlineData("pending",     "done",        "claim_task")]
    [InlineData("in_progress", "claimed",     "start_work")]
    [InlineData("done",        "in_progress", "start_work")]
    [InlineData("review",      "pending",     "release_claim")]
    public void Audit_IllegalTaskTransitions_AreRejected(string from, string to, string via)
    {
        var r = _svc.ValidateTransition(_fx.Audit, EntityType.Task, from, to, via,
            HumanActor(), "auditor", TransitionContext.Empty);
        r.Allowed.Should().BeFalse();
        r.RejectionCode.Should().Be(ErrorCodes.IllegalTransition);
        r.AllowedNext.Should().NotBeNull();
    }

    [Fact]
    public void Audit_IllegalTransitionReturnsAllowedNext()
    {
        var r = _svc.ValidateTransition(_fx.Audit, EntityType.Task, "pending", "done", "submit_findings",
            HumanActor(), "auditor", TransitionContext.Empty);
        r.Allowed.Should().BeFalse();
        r.AllowedNext.Should().NotBeNullOrEmpty("the LLM needs to know what's allowed next from 'pending'");
        r.AllowedNext.Should().Contain("claimed");
    }

    // -----------------------------------------------------------------------
    // Audit profile — role and permission checks
    // -----------------------------------------------------------------------

    [Fact]
    public void Audit_VerifierClaimingAsAuditor_IsRejected()
    {
        var r = _svc.ValidateTransition(_fx.Audit, EntityType.Task, "pending", "claimed", "claim_task",
            HumanActor(), "verifier", TransitionContext.Empty);
        r.Allowed.Should().BeFalse();
        r.RejectionCode.Should().Be(ErrorCodes.RoleNotAllowed);
        r.RequiredRoles.Should().Contain("auditor");
    }

    [Fact]
    public void Audit_DeferRequiresOverridePermission()
    {
        var actorWithoutPerm = HumanActor(canOverride: false);
        var r = _svc.ValidateTransition(_fx.Audit, EntityType.Task, "in_progress", "deferred", "defer",
            actorWithoutPerm, null, TransitionContext.Empty);
        r.Allowed.Should().BeFalse();
        r.RejectionCode.Should().Be(ErrorCodes.PermissionDenied);
        r.RequiredPermission.Should().Be("can_override_verdict");
    }

    [Fact]
    public void Audit_DeferWithPermissionSucceeds()
    {
        var actor = HumanActor(canOverride: true);
        var r = _svc.ValidateTransition(_fx.Audit, EntityType.Task, "in_progress", "deferred", "defer",
            actor, null, TransitionContext.Empty);
        r.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Audit_WildcardEscalateWorksFromAnyState()
    {
        foreach (var from in new[] { "pending", "in_progress", "review", "blocked" })
        {
            var r = _svc.ValidateTransition(_fx.Audit, EntityType.Task, from, "needs_human_review", "escalate",
                HumanActor(), null, TransitionContext.Empty);
            r.Allowed.Should().BeTrue($"escalate(*→needs_human_review) should work from {from}");
        }
    }

    // -----------------------------------------------------------------------
    // Development profile — happy path
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("pending",     "claimed",     "claim_task",   "developer")]
    [InlineData("claimed",     "in_progress", "start_work",   null)]
    [InlineData("in_progress", "review",      "submit_attempt", "developer")]
    public void Dev_TaskTransitions_LegalCases(string from, string to, string via, string? role)
    {
        var r = _svc.ValidateTransition(_fx.Development, EntityType.Task, from, to, via,
            HumanActor(), role, TransitionContext.Empty);
        r.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Dev_ReviewerApproval_ToDone()
    {
        var r = _svc.ValidateTransition(_fx.Development, EntityType.Task, "review", "done", "submit_review_decision",
            HumanActor(), "reviewer",
            new TransitionContext(Verdict: "approved"));
        r.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Dev_ChangesRequested_BelowCap_GoesBackToInProgress()
    {
        var r = _svc.ValidateTransition(_fx.Development, EntityType.Task, "review", "in_progress", "submit_review_decision",
            HumanActor(), "reviewer",
            new TransitionContext(Verdict: "changes_requested", AttemptCount: 1, RetryCap: 3));
        r.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Dev_ChangesRequested_AtCap_GoesToNeedsHumanReview()
    {
        var r = _svc.ValidateTransition(_fx.Development, EntityType.Task, "review", "needs_human_review", "submit_review_decision",
            HumanActor(), "reviewer",
            new TransitionContext(Verdict: "changes_requested", AttemptCount: 3, RetryCap: 3));
        r.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Dev_ChangesRequested_AtCap_CannotGoBackToInProgress()
    {
        var r = _svc.ValidateTransition(_fx.Development, EntityType.Task, "review", "in_progress", "submit_review_decision",
            HumanActor(), "reviewer",
            new TransitionContext(Verdict: "changes_requested", AttemptCount: 3, RetryCap: 3));
        r.Allowed.Should().BeFalse();
        r.RejectionCode.Should().Be(ErrorCodes.GuardFailed);
    }

    [Fact]
    public void Dev_DeveloperCannotApproveOwnWork()
    {
        var r = _svc.ValidateTransition(_fx.Development, EntityType.Task, "review", "done", "submit_review_decision",
            HumanActor(), "developer",
            new TransitionContext(Verdict: "approved"));
        r.Allowed.Should().BeFalse();
        r.RejectionCode.Should().Be(ErrorCodes.RoleNotAllowed);
    }

    [Fact]
    public void Dev_HasNoFindingTransitions()
    {
        _fx.Development.FindingStates.Should().BeEmpty();
        _fx.Development.FindingTransitions.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Board column mapping
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("pending", "backlog")]
    [InlineData("in_progress", "in_progress")]
    [InlineData("review", "review")]
    [InlineData("done", "done")]
    [InlineData("blocked", "blocked")]
    [InlineData("needs_human_review", "blocked")]
    [InlineData("skipped", "done")]
    public void Audit_BoardColumnMapping(string status, string expectedColumn)
    {
        StateMachineService.ResolveBoardColumn(_fx.Audit, status).Should().Be(expectedColumn);
    }

    [Fact]
    public void BoardColumnMapping_PerPlanOverrideWins()
    {
        var planOverride = new Dictionary<string, string> { ["pending"] = "icebox" };
        StateMachineService.ResolveBoardColumn(_fx.Audit, "pending", planOverride).Should().Be("icebox");
    }

    [Fact]
    public void BoardColumnMapping_UnknownStatusFallsBackToBacklog()
    {
        StateMachineService.ResolveBoardColumn(_fx.Audit, "unmapped_status").Should().Be("backlog");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static RequestContext HumanActor(bool canOverride = false, bool isAdmin = false)
    {
        return new RequestContext(
            ActorId: Guid.NewGuid(),
            ActorType: ActorType.Human,
            GithubUsername: "test-user",
            IsAdmin: isAdmin,
            CanOverrideVerdict: canOverride,
            CanArchivePlan: false,
            CanMarkNeedsHumanReview: true,
            TraceId: "test-trace",
            Now: DateTimeOffset.UtcNow);
    }
}
