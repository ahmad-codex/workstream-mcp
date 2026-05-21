using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Workstream.Slack;

/// <summary>
/// Builds a Slack modal <c>view</c> payload (as a JSON string) for the detail pane that
/// opens when a notification's "View details" button is clicked. The layout is a header,
/// a two-column field grid, a run of labelled body sections, and a context footer —
/// the same visual language as the notification card, with room for the fuller detail
/// (a finding's root cause, repro steps, expected vs actual …).
/// </summary>
public static class SlackModalBuilder
{
    private const int TitleMax   = 24;    // Slack modal title plain_text limit
    private const int HeaderMax  = 150;
    private const int FieldMax   = 1900;
    private const int SectionMax = 2900;

    /// <summary>A labelled body section. <see cref="Label"/> is bolded above the text.</summary>
    public readonly record struct Section(string? Label, string Text);

    /// <summary>
    /// Render the modal view. <paramref name="title"/> is the chrome title (kept short);
    /// <paramref name="header"/> is the in-body header and may carry <c>:emoji:</c>.
    /// Empty-text sections are skipped.
    /// </summary>
    public static string Build(
        string title,
        string header,
        IReadOnlyList<SlackBlockKitBuilder.Field> fields,
        IReadOnlyList<Section> sections,
        string? context)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", "modal");

            w.WriteStartObject("title");
            w.WriteString("type", "plain_text");
            w.WriteString("text", Trunc(title, TitleMax));
            w.WriteEndObject();

            w.WriteStartObject("close");
            w.WriteString("type", "plain_text");
            w.WriteString("text", "Close");
            w.WriteEndObject();

            w.WriteStartArray("blocks");

            // Header.
            w.WriteStartObject();
            w.WriteString("type", "header");
            w.WriteStartObject("text");
            w.WriteString("type", "plain_text");
            w.WriteString("text", Trunc(header, HeaderMax));
            w.WriteBoolean("emoji", true);
            w.WriteEndObject();
            w.WriteEndObject();

            // Field grid.
            if (fields.Count > 0)
            {
                w.WriteStartObject();
                w.WriteString("type", "section");
                w.WriteStartArray("fields");
                var n = Math.Min(fields.Count, 10);
                for (var i = 0; i < n; i++)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "mrkdwn");
                    w.WriteString("text", Trunc($"*{fields[i].Label}*\n{fields[i].Value}", FieldMax));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }

            // Labelled body sections, each under a divider.
            foreach (var s in sections)
            {
                if (string.IsNullOrWhiteSpace(s.Text)) continue;

                w.WriteStartObject();
                w.WriteString("type", "divider");
                w.WriteEndObject();

                w.WriteStartObject();
                w.WriteString("type", "section");
                w.WriteStartObject("text");
                w.WriteString("type", "mrkdwn");
                var text = string.IsNullOrWhiteSpace(s.Label)
                    ? s.Text.Trim()
                    : $"*{s.Label}*\n{s.Text.Trim()}";
                w.WriteString("text", Trunc(text, SectionMax));
                w.WriteEndObject();
                w.WriteEndObject();
            }

            // Context footer.
            if (!string.IsNullOrWhiteSpace(context))
            {
                w.WriteStartObject();
                w.WriteString("type", "context");
                w.WriteStartArray("elements");
                w.WriteStartObject();
                w.WriteString("type", "mrkdwn");
                w.WriteString("text", Trunc(context!.Trim(), SectionMax));
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Trunc(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";
}
