using System;
using System.Security.Cryptography;
using System.Text;

namespace Workstream.GitHub;

/// <summary>
/// GitHub webhook signature verification (§13.4). HMAC-SHA256 of the body keyed by the
/// app's webhook secret, header <c>X-Hub-Signature-256</c>. Constant-time compare to
/// avoid timing oracles.
/// </summary>
public static class WebhookSignatureVerifier
{
    public static bool Verify(byte[] body, string headerValue, string secret)
    {
        if (string.IsNullOrEmpty(headerValue) || string.IsNullOrEmpty(secret)) return false;
        var prefix = "sha256=";
        if (!headerValue.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var providedHex = headerValue[prefix.Length..];
        byte[] provided;
        try { provided = Convert.FromHexString(providedHex); } catch { return false; }
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = hmac.ComputeHash(body);
        return CryptographicOperations.FixedTimeEquals(provided, computed);
    }
}
