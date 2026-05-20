using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Workstream.Slack;

/// <summary>
/// Minimal Slack Web API client for <c>chat.postMessage</c>. Avoids SlackNet's heavier
/// abstractions for the two endpoints we actually need. The bot token is resolved per-call
/// from <see cref="ISlackBotTokenResolver"/> so per-project tokens stay scoped.
/// </summary>
public sealed class SlackClient
{
    private readonly HttpClient _http;

    public SlackClient(HttpClient http)
    {
        _http = http;
        if (_http.BaseAddress is null) _http.BaseAddress = new Uri("https://slack.com/api/");
    }

    /// <summary>
    /// Post a message. When <paramref name="color"/> is a non-empty hex string the body
    /// is wrapped in a Slack attachment so the message renders with a coloured left bar
    /// (keyed to the acting role); otherwise it posts as a plain mrkdwn message. When
    /// <paramref name="authorName"/> is supplied the attachment also carries an author
    /// row — for a human actor that is their real GitHub avatar, name, and profile link.
    /// </summary>
    public async Task<SlackPostResult> PostMessageAsync(
        string botToken, string channelId, string text, string? threadTs,
        string? color = null, string? authorName = null, string? authorIcon = null, string? authorLink = null,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat.postMessage");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
        object payload;
        if (string.IsNullOrEmpty(color))
        {
            payload = new
            {
                channel = channelId,
                text,
                thread_ts = threadTs,
                unfurl_links = false,
                unfurl_media = false,
            };
        }
        else
        {
            // Build the attachment as a dictionary so the optional author row (a human
            // actor's GitHub avatar + name + profile link) can be added conditionally.
            var attachment = new Dictionary<string, object?>
            {
                ["color"]     = color,
                ["text"]      = text,
                ["mrkdwn_in"] = new[] { "text" },
                ["fallback"]  = PlainFallback(text),
            };
            if (!string.IsNullOrWhiteSpace(authorName)) attachment["author_name"] = authorName;
            if (!string.IsNullOrWhiteSpace(authorIcon)) attachment["author_icon"] = authorIcon;
            if (!string.IsNullOrWhiteSpace(authorLink)) attachment["author_link"] = authorLink;
            payload = new
            {
                channel = channelId,
                attachments = new[] { attachment },
                thread_ts = threadTs,
                unfurl_links = false,
                unfurl_media = false,
            };
        }
        req.Content = JsonContent.Create(payload);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var content = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"slack HTTP {(int)resp.StatusCode}: {content}");
        using var doc = JsonDocument.Parse(content);
        var ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
        if (!ok)
        {
            var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
            return new SlackPostResult(false, null, err);
        }
        var ts = doc.RootElement.TryGetProperty("ts", out var t) ? t.GetString() : null;
        return new SlackPostResult(true, ts, null);
    }

    /// <summary>
    /// Plain-text fallback for an attachment (shown in notifications and older clients):
    /// collapse mrkdwn links &lt;url|label&gt; to just the label.
    /// </summary>
    private static string PlainFallback(string text)
        => Regex.Replace(text, @"<[^|>]+\|([^>]+)>", "$1");
}

public sealed record SlackPostResult(bool Ok, string? Ts, string? Error);

/// <summary>
/// Resolves per-project Slack bot tokens from their <c>bot_token_secret_ref</c>. Default
/// impl reads file paths (referenced via <c>file://</c> or absolute) from disk. A
/// <c>vault://</c> ref would hit HashiCorp Vault — left for production.
/// </summary>
public interface ISlackBotTokenResolver
{
    Task<string> ResolveAsync(string secretRef, CancellationToken ct = default);
}

public sealed class FileSlackBotTokenResolver : ISlackBotTokenResolver
{
    public async Task<string> ResolveAsync(string secretRef, CancellationToken ct = default)
    {
        var path = secretRef.StartsWith("file://", StringComparison.Ordinal) ? secretRef[7..] : secretRef;
        if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("empty Slack secret ref");
        return (await System.IO.File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Trim();
    }
}
