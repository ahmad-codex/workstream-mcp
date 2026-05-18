using FluentAssertions;
using Workstream.Core.StateMachine;
using Workstream.UnitTests.Fixtures;
using Xunit;

namespace Workstream.UnitTests;

public sealed class StateGraphParserTests : IClassFixture<PlanProfileFixture>
{
    private readonly PlanProfileFixture _fx;

    public StateGraphParserTests(PlanProfileFixture fx) => _fx = fx;

    [Fact]
    public void Audit_TaskStateList_MatchesSpec()
    {
        _fx.Audit.TaskStates.Should().Contain(new[]
        {
            "pending","claimed","in_progress","review","done",
            "deferred","blocked","needs_human_review","skipped","out_of_scope"
        });
    }

    [Fact]
    public void Audit_FindingStateList_MatchesSpec()
    {
        _fx.Audit.FindingStates.Should().Contain(new[]
        {
            "pending_verification","confirmed","rejected","ambiguous",
            "in_fix","fixed","fix_failed","partial","needs_human_review","deferred"
        });
    }

    [Fact]
    public void Audit_TerminalTaskStates_AreCorrect()
    {
        _fx.Audit.TaskTerminal.Should().BeEquivalentTo(new[]
        {
            "done","deferred","needs_human_review","skipped","out_of_scope"
        });
    }

    [Fact]
    public void Audit_BoardColumnMapping_Loaded()
    {
        _fx.Audit.BoardColumnMapping["in_progress"].Should().Be("in_progress");
        _fx.Audit.BoardColumnMapping["needs_human_review"].Should().Be("blocked");
    }

    [Fact]
    public void Development_HasNoFindingsModel()
    {
        _fx.Development.FindingStates.Should().BeEmpty();
        _fx.Development.FindingTerminal.Should().BeEmpty();
        _fx.Development.FindingTransitions.Should().BeEmpty();
    }

    [Fact]
    public void Development_TaskInitialIsPending()
    {
        _fx.Development.TaskInitial.Should().Be("pending");
    }

    [Fact]
    public void Audit_WildcardEscalateExists()
    {
        var hasWildcardEscalate = false;
        foreach (var t in _fx.Audit.TaskTransitions)
            if (t.From == "*" && t.To == "needs_human_review" && t.Via == "escalate")
                hasWildcardEscalate = true;
        hasWildcardEscalate.Should().BeTrue();
    }
}
