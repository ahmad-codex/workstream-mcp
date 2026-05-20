using Microsoft.Extensions.DependencyInjection;

namespace Workstream.Mcp;

/// <summary>
/// One-call DI registration for the entire MCP tool surface. The host (Workstream.Api or any
/// alternative front-end) calls <see cref="AddWorkstreamMcpTools"/> after registering the
/// underlying repositories and the <see cref="Workstream.Core.StateMachine.StateMachineService"/>.
/// </summary>
public static class McpToolRegistration
{
    public static IServiceCollection AddWorkstreamMcpTools(this IServiceCollection services)
    {
        // Discovery
        services.AddSingleton<IMcpTool, Tools.Discovery.ListProjectsTool>();
        services.AddSingleton<IMcpTool, Tools.Discovery.ListPlansTool>();
        services.AddSingleton<IMcpTool, Tools.Discovery.GetPlanDashboardTool>();
        services.AddSingleton<IMcpTool, Tools.Discovery.GetMyActiveWorkTool>();

        // Claims
        services.AddSingleton<IMcpTool, Tools.Claims.ClaimNextTaskTool>();
        services.AddSingleton<IMcpTool, Tools.Claims.ClaimSpecificTaskTool>();
        services.AddSingleton<IMcpTool, Tools.Claims.ClaimNextFindingForVerificationTool>();
        services.AddSingleton<IMcpTool, Tools.Claims.ClaimNextFindingForFixTool>();
        services.AddSingleton<IMcpTool, Tools.Claims.ClaimNextAttemptForReviewTool>();
        services.AddSingleton<IMcpTool, Tools.Claims.ReleaseClaimTool>();
        services.AddSingleton<IMcpTool, Tools.Claims.RefreshClaimTool>();

        // Submission
        services.AddSingleton<IMcpTool, Tools.Submission.StartWorkTool>();
        services.AddSingleton<IMcpTool, Tools.Submission.SubmitFindingsTool>();
        services.AddSingleton<IMcpTool, Tools.Submission.SubmitVerificationVerdictTool>();
        services.AddSingleton<IMcpTool, Tools.Submission.SubmitAttemptTool>();
        services.AddSingleton<IMcpTool, Tools.Submission.SubmitAttemptVerdictTool>();
        services.AddSingleton<IMcpTool, Tools.Submission.SubmitReviewDecisionTool>();

        // Lifecycle
        services.AddSingleton<IMcpTool, Tools.Lifecycle.MarkTaskStatusTool>();
        services.AddSingleton<IMcpTool, Tools.Lifecycle.RecordCommitTool>();
        services.AddSingleton<IMcpTool, Tools.Lifecycle.CreateTaskTool>();
        services.AddSingleton<IMcpTool, Tools.Lifecycle.CreateTasksTool>();
        services.AddSingleton<IMcpTool, Tools.Lifecycle.OverrideVerdictTool>();

        // Forensic
        services.AddSingleton<IMcpTool, Tools.Forensic.GetEventLogTool>();
        services.AddSingleton<IMcpTool, Tools.Forensic.GetFindingTool>();
        services.AddSingleton<IMcpTool, Tools.Forensic.GetStuckWorkTool>();
        services.AddSingleton<IMcpTool, Tools.Forensic.ExportPlanTool>();

        // Setup (admin)
        services.AddSingleton<IMcpTool, Tools.Setup.CreateProjectTool>();
        services.AddSingleton<IMcpTool, Tools.Setup.AddProjectRepoTool>();
        services.AddSingleton<IMcpTool, Tools.Setup.AddProjectBoardTool>();
        services.AddSingleton<IMcpTool, Tools.Setup.SetProjectSlackTool>();
        services.AddSingleton<IMcpTool, Tools.Setup.CreatePlanTool>();
        services.AddSingleton<IMcpTool, Tools.Setup.AddPhaseTool>();
        services.AddSingleton<IMcpTool, Tools.Setup.ActivatePlanTool>();
        services.AddSingleton<IMcpTool, Tools.Setup.ArchivePlanTool>();

        return services;
    }
}
