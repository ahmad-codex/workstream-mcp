using System;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Workstream.Slack;
using Xunit;

namespace Workstream.UnitTests;

/// <summary>
/// Covers <see cref="SlackSignatureVerifier"/>: a correctly-signed recent request passes;
/// a wrong secret, a tampered body, a stale timestamp, or a malformed header all fail.
/// </summary>
public sealed class SlackSignatureVerifierTests
{
    private const string Secret = "8f742231b10e8888abcd99yyyzzz85a5";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000);

    /// <summary>Produce the header value Slack would send for this body + timestamp + secret.</summary>
    private static string Sign(byte[] body, string timestamp, string secret)
    {
        var head = Encoding.UTF8.GetBytes($"v0:{timestamp}:");
        var basestring = new byte[head.Length + body.Length];
        Buffer.BlockCopy(head, 0, basestring, 0, head.Length);
        Buffer.BlockCopy(body, 0, basestring, head.Length, body.Length);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "v0=" + Convert.ToHexString(hmac.ComputeHash(basestring)).ToLowerInvariant();
    }

    private static byte[] Body() => Encoding.UTF8.GetBytes("payload=%7B%22type%22%3A%22block_actions%22%7D");

    [Fact]
    public void Accepts_a_correctly_signed_recent_request()
    {
        var body = Body();
        var ts = Now.ToUnixTimeSeconds().ToString();
        SlackSignatureVerifier.Verify(body, ts, Sign(body, ts, Secret), Secret, Now)
            .Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_wrong_secret()
    {
        var body = Body();
        var ts = Now.ToUnixTimeSeconds().ToString();
        SlackSignatureVerifier.Verify(body, ts, Sign(body, ts, "the-wrong-secret"), Secret, Now)
            .Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_tampered_body()
    {
        var ts = Now.ToUnixTimeSeconds().ToString();
        var sig = Sign(Body(), ts, Secret);
        var tampered = Encoding.UTF8.GetBytes("payload=%7B%22type%22%3A%22evil%22%7D");
        SlackSignatureVerifier.Verify(tampered, ts, sig, Secret, Now).Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_stale_timestamp()
    {
        var body = Body();
        // Signed 10 minutes ago — beyond the 5-minute replay window.
        var stale = Now.AddMinutes(-10);
        var ts = stale.ToUnixTimeSeconds().ToString();
        SlackSignatureVerifier.Verify(body, ts, Sign(body, ts, Secret), Secret, Now)
            .Should().BeFalse();
    }

    [Fact]
    public void Rejects_an_empty_signing_secret()
    {
        var body = Body();
        var ts = Now.ToUnixTimeSeconds().ToString();
        SlackSignatureVerifier.Verify(body, ts, Sign(body, ts, Secret), "", Now)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("deadbeef")]          // missing v0= prefix
    [InlineData("v0=nothex")]         // not hex
    public void Rejects_a_malformed_signature_header(string header)
    {
        var body = Body();
        var ts = Now.ToUnixTimeSeconds().ToString();
        SlackSignatureVerifier.Verify(body, ts, header, Secret, Now).Should().BeFalse();
    }
}
