namespace Workstream.GitHub;

public sealed class GitHubAppOptions
{
    /// <summary>GitHub App id, configured via <c>WORKSTREAM_GH_APP_ID</c>.</summary>
    public int AppId { get; set; }

    /// <summary>Path on disk to the App's private key (.pem). Mounted via secret in production.</summary>
    public string PrivateKeyPath { get; set; } = "";

    /// <summary>App installation id for the org. Resolved at first use; can be cached.</summary>
    public long InstallationId { get; set; }

    /// <summary>Webhook secret for signature verification (§13.4).</summary>
    public string WebhookSecret { get; set; } = "";
}
