namespace Workstream.Slack;

/// <summary>
/// App-level Slack configuration. Unlike the per-project bot token (resolved from
/// <c>project_slack.bot_token_secret_ref</c>), the signing secret belongs to the single
/// Slack app and is shared across every workspace the app is installed in — so it is one
/// process-wide value, configured via <c>WORKSTREAM_SLACK_SIGNING_SECRET</c>.
/// </summary>
public sealed class SlackAppOptions
{
    /// <summary>
    /// The Slack app signing secret used to verify inbound interactivity requests
    /// (<c>X-Slack-Signature</c>). Empty until configured — while empty every inbound
    /// interaction is rejected, so the "View details" button is simply inert.
    /// </summary>
    public string SigningSecret { get; set; } = "";
}
