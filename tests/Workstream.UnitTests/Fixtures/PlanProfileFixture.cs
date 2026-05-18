using System;
using System.IO;
using System.Text.Json;
using Workstream.Core.Domain;
using Workstream.Core.StateMachine;

namespace Workstream.UnitTests.Fixtures;

/// <summary>
/// Loads the two seed profile JSON files from the build-output <c>profiles/</c> folder
/// and exposes parsed <see cref="StateGraph"/> instances + <see cref="PlanType"/> rows.
/// Reused across state-machine tests so every test exercises the actual shipped profiles.
/// </summary>
public sealed class PlanProfileFixture
{
    public StateGraph Audit       { get; }
    public StateGraph Development { get; }
    public PlanType   AuditType   { get; }
    public PlanType   DevType     { get; }
    public InMemoryPlanTypeCache Cache { get; }

    public PlanProfileFixture()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "profiles");
        if (!Directory.Exists(dir))
            throw new InvalidOperationException(
                $"profiles directory not found at {dir} — check Workstream.UnitTests.csproj copies deploy/profiles/*.json");

        AuditType = LoadPlanType(Path.Combine(dir, "audit.json"));
        DevType   = LoadPlanType(Path.Combine(dir, "development.json"));

        Audit       = StateGraphParser.Parse(AuditType.StateGraphJson);
        Development = StateGraphParser.Parse(DevType.StateGraphJson);

        Cache = new InMemoryPlanTypeCache();
        Cache.Set(AuditType);
        Cache.Set(DevType);
    }

    private static PlanType LoadPlanType(string path)
    {
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var id          = root.GetProperty("id").GetString()!;
        var displayName = root.GetProperty("display_name").GetString()!;
        var requires    = root.GetProperty("requires_findings").GetBoolean();
        var retryCap    = root.GetProperty("retry_cap").GetInt32();
        var roleTtls    = root.GetProperty("role_ttls").GetRawText();
        var stateGraph  = root.GetProperty("state_graph").GetRawText();
        var boardMap    = root.GetProperty("state_graph").GetProperty("board_column_mapping").GetRawText();
        // The profile JSON nests slack_templates etc. at top level; pack the rest into config.
        var config      = "{}";
        var now         = DateTimeOffset.UtcNow;

        return new PlanType
        {
            Id                     = id,
            DisplayName            = displayName,
            StateGraphJson         = stateGraph,
            RoleTtlsJson           = roleTtls,
            RetryCap               = retryCap,
            PromptTemplateRef      = null,
            RequiresFindings       = requires,
            BoardColumnMappingJson = boardMap,
            ConfigJson             = config,
            CreatedAt              = now,
            UpdatedAt              = now,
        };
    }
}
