using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GitHubJwt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Octokit;

namespace Workstream.GitHub;

/// <summary>
/// Mints short-lived installation tokens for the org's GitHub App (§7.2). Caches the
/// installation token for ~50 minutes (the actual TTL is 60 minutes) to avoid the JWT-then-
/// installation-token round-trip on every call.
/// </summary>
public sealed class GitHubAppAuthService
{
    private readonly IOptions<GitHubAppOptions> _opts;
    private readonly ILogger<GitHubAppAuthService>? _log;

    private string? _cachedToken;
    private DateTimeOffset _cachedUntil;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public GitHubAppAuthService(IOptions<GitHubAppOptions> opts, ILogger<GitHubAppAuthService>? log = null)
    {
        _opts = opts;
        _log = log;
    }

    public async Task<string> GetInstallationTokenAsync(CancellationToken ct = default)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _cachedUntil) return _cachedToken;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _cachedUntil) return _cachedToken;

            var opts = _opts.Value;
            if (opts.AppId == 0 || string.IsNullOrEmpty(opts.PrivateKeyPath))
                throw new InvalidOperationException("GitHubAppOptions.AppId and PrivateKeyPath must be configured");
            if (!File.Exists(opts.PrivateKeyPath))
                throw new FileNotFoundException("GitHub App private key not found", opts.PrivateKeyPath);

            var jwtFactory = new GitHubJwtFactory(
                new FilePrivateKeySource(opts.PrivateKeyPath),
                new GitHubJwtFactoryOptions
                {
                    AppIntegrationId = opts.AppId,
                    ExpirationSeconds = 540,    // 9 minutes (GitHub allows ≤ 10)
                });
            var jwt = jwtFactory.CreateEncodedJwtToken();
            var appClient = new GitHubClient(new ProductHeaderValue("workstream-mcp"))
            {
                Credentials = new Credentials(jwt, AuthenticationType.Bearer),
            };

            var installationToken = await appClient.GitHubApps
                .CreateInstallationToken(opts.InstallationId)
                .ConfigureAwait(false);

            _cachedToken = installationToken.Token;
            _cachedUntil = installationToken.ExpiresAt.AddMinutes(-5);   // 5-minute safety margin
            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate()
    {
        _cachedToken = null;
        _cachedUntil = DateTimeOffset.MinValue;
    }
}
