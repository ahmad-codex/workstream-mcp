using System;
using System.Security.Cryptography;
using System.Text;

namespace Workstream.Slack;

/// <summary>
/// Verifies the signature on an inbound Slack request (interactivity, events, slash
/// commands). Slack signs each request with HMAC-SHA256 over the string
/// <c>v0:{timestamp}:{rawBody}</c> keyed by the app signing secret, and sends the result
/// in <c>X-Slack-Signature</c> as <c>v0=&lt;hex&gt;</c> alongside an
/// <c>X-Slack-Request-Timestamp</c> header. See
/// https://docs.slack.dev/authentication/verifying-requests-from-slack.
///
/// Mirrors the GitHub <c>WebhookSignatureVerifier</c>; the differences are the basestring
/// shape and the timestamp replay guard.
/// </summary>
public static class SlackSignatureVerifier
{
    /// <summary>Reject a request whose timestamp is more than this far from now (replay guard).</summary>
    private const long MaxSkewSeconds = 300;

    /// <summary>
    /// True when <paramref name="signatureHeader"/> is a valid signature for
    /// <paramref name="body"/> at <paramref name="timestampHeader"/> under
    /// <paramref name="signingSecret"/>, and the timestamp is recent. A constant-time
    /// compare avoids leaking the secret through timing. <paramref name="now"/> is for tests.
    /// </summary>
    public static bool Verify(
        byte[] body, string timestampHeader, string signatureHeader, string signingSecret,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrEmpty(signingSecret)) return false;
        if (string.IsNullOrEmpty(timestampHeader) || string.IsNullOrEmpty(signatureHeader)) return false;
        if (!long.TryParse(timestampHeader, out var ts)) return false;

        var nowUnix = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        if (Math.Abs(nowUnix - ts) > MaxSkewSeconds) return false;

        const string prefix = "v0=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.Ordinal)) return false;
        byte[] provided;
        try { provided = Convert.FromHexString(signatureHeader[prefix.Length..]); }
        catch { return false; }

        // basestring = "v0:" + timestamp + ":" + rawBody — HMAC over the exact bytes.
        var head = Encoding.UTF8.GetBytes($"v0:{timestampHeader}:");
        var basestring = new byte[head.Length + body.Length];
        Buffer.BlockCopy(head, 0, basestring, 0, head.Length);
        Buffer.BlockCopy(body, 0, basestring, head.Length, body.Length);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
        var computed = hmac.ComputeHash(basestring);
        return CryptographicOperations.FixedTimeEquals(provided, computed);
    }
}
