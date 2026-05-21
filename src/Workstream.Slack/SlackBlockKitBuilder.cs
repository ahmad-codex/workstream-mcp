using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Workstream.Slack;

/// <summary>
/// Builds the Slack Block Kit <c>blocks</c> array (as a JSON string) for a workflow
/// notification. The result is stored on <c>slack_notify_log.blocks_json</c> at enqueue
/// time and posted inside the role-coloured attachment by the SlackNotifyWorker.
///
/// The layout is fixed: a <c>header</c>, a two-column <c>section.fields</c> grid
/// (Project / Plan / Task / Status …), an optional body <c>section</c>, an optional
/// persona <c>context</c> line, then a <c>divider</c> and an <c>actions</c> button row.
/// All caller text is written through <see cref="System.Text.Json.Utf8JsonWriter"/> so
/// it is escaped.
/// </summary>
public static class SlackBlockKitBuilder
{
    private const int HeaderMax  = 150;   // Slack header plain_text limit
    private const int FieldMax   = 1900;  // section field text limit is 2000
    private const int SectionMax = 2900;  // section text limit is 3000
    private const int ButtonMax  = 75;    // button plain_text limit
    private const int ValueMax   = 2000;  // button value limit

    /// <summary>A label / value pair rendered as one cell of the field grid.</summary>
    public readonly record struct Field(string Label, string Value);

    /// <summary>
    /// An <c>actions</c>-row button. A <see cref="Url"/> button opens a link in the
    /// browser with no callback; an action button carries an <see cref="ActionId"/> and
    /// <see cref="Value"/> that the interactivity endpoint receives on click. Set
    /// <see cref="Style"/> to <c>primary</c> / <c>danger</c> to colour the button.
    /// </summary>
    public readonly record struct Button(
        string Label, string? Url, string? ActionId, string? Value, string? Style)
    {
        /// <summary>A browser deep-link button.</summary>
        public static Button Link(string label, string url) => new(label, url, null, null, null);

        /// <summary>A callback button — the click is delivered to the interactivity endpoint.</summary>
        public static Button Action(string label, string actionId, string value, string? style = null)
            => new(label, null, actionId, value, style);
    }

    /// <summary>
    /// Render the blocks array. <paramref name="header"/> may contain <c>:emoji:</c>
    /// shortcodes. <paramref name="context"/> and <paramref name="body"/> are mrkdwn and
    /// skipped when blank. Up to 10 fields and 5 buttons are emitted.
    /// </summary>
    public static string Build(
        string header,
        IReadOnlyList<Field> fields,
        string? context,
        string? body,
        IReadOnlyList<Button> buttons)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartArray();

            // Header — plain_text; emoji:true renders :shortcode: emoji.
            w.WriteStartObject();
            w.WriteString("type", "header");
            w.WriteStartObject("text");
            w.WriteString("type", "plain_text");
            w.WriteString("text", Trunc(header, HeaderMax));
            w.WriteBoolean("emoji", true);
            w.WriteEndObject();
            w.WriteEndObject();

            // Field grid — Slack lays mrkdwn fields out in two columns.
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

            // Body section — description / reason / commit.
            if (!string.IsNullOrWhiteSpace(body))
            {
                w.WriteStartObject();
                w.WriteString("type", "section");
                w.WriteStartObject("text");
                w.WriteString("type", "mrkdwn");
                w.WriteString("text", Trunc(body!.Trim(), SectionMax));
                w.WriteEndObject();
                w.WriteEndObject();
            }

            // Context — persona / actor line (empty for human actors: the attachment
            // author row carries their identity instead).
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

            // Actions — buttons under a divider.
            if (buttons.Count > 0)
            {
                w.WriteStartObject();
                w.WriteString("type", "divider");
                w.WriteEndObject();

                w.WriteStartObject();
                w.WriteString("type", "actions");
                w.WriteStartArray("elements");
                var n = Math.Min(buttons.Count, 5);
                for (var i = 0; i < n; i++)
                {
                    var b = buttons[i];
                    w.WriteStartObject();
                    w.WriteString("type", "button");
                    w.WriteStartObject("text");
                    w.WriteString("type", "plain_text");
                    w.WriteString("text", Trunc(b.Label, ButtonMax));
                    w.WriteBoolean("emoji", true);
                    w.WriteEndObject();
                    // action_id is required and must be unique within the message.
                    w.WriteString("action_id", b.ActionId ?? $"open_link_{i}");
                    if (b.Url is not null)
                        w.WriteString("url", b.Url);
                    if (b.Value is not null)
                        w.WriteString("value", Trunc(b.Value, ValueMax));
                    if (b.Style is not null)
                        w.WriteString("style", b.Style);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Trunc(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";
}
