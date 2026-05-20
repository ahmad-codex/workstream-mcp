using System;
using System.Collections.Generic;
using FluentAssertions;
using Workstream.Core.Domain;
using Workstream.GitHub;
using Xunit;
using TaskStatus = Workstream.Core.Domain.TaskStatus;

namespace Workstream.UnitTests;

/// <summary>
/// Covers the audit-ledger renderer that writes finding-level lifecycle into a task's
/// Project V2 card body, and the coarse stage computation behind the Audit Stage field.
/// </summary>
public sealed class AuditLedgerTests
{
    private static WorkTask Task(string status, string title = "Query semantics", string? desc = "Audit the query path")
        => new(Guid.NewGuid(), Guid.NewGuid(), null, "T-4", title, desc, null, null, 1, status,
               null, null, null, null, ClaimState.Unclaimed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static Finding Finding(string key, string status, string? severity = "high", string? symptom = "a symptom")
        => new(Guid.NewGuid(), Guid.NewGuid(), key, severity, null, symptom, null, null, null, null, null, null,
               status, ClaimState.Unclaimed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(TaskStatus.Blocked,          FindingStatus.Confirmed,           "Blocked")]
    [InlineData(TaskStatus.NeedsHumanReview, FindingStatus.Confirmed,           "Escalated")]
    [InlineData(TaskStatus.InProgress,       FindingStatus.PendingVerification, "Verifying")]
    [InlineData(TaskStatus.Review,           FindingStatus.Ambiguous,           "Verifying")]
    [InlineData(TaskStatus.Review,           FindingStatus.Confirmed,           "Fixing")]
    [InlineData(TaskStatus.Review,           FindingStatus.InFix,               "Fixing")]
    [InlineData(TaskStatus.Done,             FindingStatus.Fixed,               "Done")]
    public void ComputeStage_picks_the_expected_stage(string taskStatus, string findingStatus, string expected)
    {
        AuditLedger.ComputeStage(taskStatus, new[] { Finding("F-T-4-1", findingStatus) })
            .Should().Be(expected);
    }

    [Fact]
    public void ComputeStage_no_findings_in_progress_is_Scanning()
        => AuditLedger.ComputeStage(TaskStatus.InProgress, Array.Empty<Finding>()).Should().Be("Scanning");

    [Fact]
    public void ComputeStage_escalates_when_any_finding_needs_human_review()
        => AuditLedger.ComputeStage(TaskStatus.Review, new[]
        {
            Finding("F-1", FindingStatus.Fixed),
            Finding("F-2", FindingStatus.NeedsHumanReview),
        }).Should().Be("Escalated");

    [Fact]
    public void Render_lists_findings_with_markers_description_and_commit()
    {
        var fixedFinding = Finding("F-T-4-1", FindingStatus.Fixed, symptom: "NaN vectors crash the query path");
        var inFix        = Finding("F-T-4-2", FindingStatus.InFix, symptom: "dimension check bypassed");

        var body = AuditLedger.Render(
            Task(TaskStatus.Review),
            new[] { fixedFinding, inFix },
            new Dictionary<Guid, string> { [fixedFinding.Id] = "abc1234567" },
            blockReason: null,
            now: DateTimeOffset.UtcNow);

        body.Should().Contain("T-4");
        body.Should().Contain("Audit the query path");      // original description is preserved
        body.Should().Contain("Audit ledger");
        body.Should().Contain("F-T-4-1");
        body.Should().Contain("F-T-4-2");
        body.Should().Contain("[x]");                        // a fixed finding renders as done
        body.Should().Contain("[ ]");                        // an in-fix finding renders as open
        body.Should().Contain("commit `abc1234567`");
        body.Should().Contain("NaN vectors crash the query path");
    }

    [Fact]
    public void Render_shows_a_block_banner_with_the_reason()
    {
        var body = AuditLedger.Render(
            Task(TaskStatus.Blocked),
            new[] { Finding("F-T-4-1", FindingStatus.Confirmed) },
            new Dictionary<Guid, string>(),
            blockReason: "upstream dependency is broken",
            now: DateTimeOffset.UtcNow);

        body.Should().Contain("Blocked:");
        body.Should().Contain("upstream dependency is broken");
    }

    [Fact]
    public void Render_marks_a_needs_human_review_finding_distinctly()
    {
        var body = AuditLedger.Render(
            Task(TaskStatus.Review),
            new[] { Finding("F-T-4-9", FindingStatus.NeedsHumanReview) },
            new Dictionary<Guid, string>(),
            blockReason: null,
            now: DateTimeOffset.UtcNow);

        body.Should().Contain("⚠️");
        body.Should().Contain("needs human review");
    }
}
