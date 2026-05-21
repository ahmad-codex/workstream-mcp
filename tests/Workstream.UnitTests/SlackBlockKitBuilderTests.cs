using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using Workstream.Slack;
using Xunit;

namespace Workstream.UnitTests;

/// <summary>
/// Covers <see cref="SlackBlockKitBuilder"/>: the rendered JSON must be a valid Block Kit
/// array, omit empty sections, cap repeated elements, and escape caller text.
/// </summary>
public sealed class SlackBlockKitBuilderTests
{
    private static readonly IReadOnlyList<SlackBlockKitBuilder.Field> SampleFields = new[]
    {
        new SlackBlockKitBuilder.Field("Project", "Acme Web"),
        new SlackBlockKitBuilder.Field("Plan", "Q2 Audit"),
    };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Emits_header_fields_context_and_actions_in_order()
    {
        var json = SlackBlockKitBuilder.Build(
            ":white_check_mark: Task done",
            SampleFields,
            ":robot_face: *Smith* · Auditor",
            "A short description.",
            new[] { SlackBlockKitBuilder.Button.Action(":mag: View details", "view_details", "task|p|e", "primary") });

        var root = Parse(json);
        root.ValueKind.Should().Be(JsonValueKind.Array);

        var types = new List<string>();
        foreach (var b in root.EnumerateArray())
            types.Add(b.GetProperty("type").GetString()!);

        types.Should().Equal("header", "section", "section", "context", "divider", "actions");
    }

    [Fact]
    public void Skips_empty_body_context_and_actions()
    {
        var json = SlackBlockKitBuilder.Build(
            "Header", SampleFields, context: null, body: "  ",
            buttons: new List<SlackBlockKitBuilder.Button>());

        var types = new List<string>();
        foreach (var b in Parse(json).EnumerateArray())
            types.Add(b.GetProperty("type").GetString()!);

        // Only header + the field section — no body, context, divider or actions.
        types.Should().Equal("header", "section");
    }

    [Fact]
    public void Caps_fields_at_ten_and_buttons_at_five()
    {
        var fields = new List<SlackBlockKitBuilder.Field>();
        for (var i = 0; i < 14; i++) fields.Add(new($"L{i}", $"V{i}"));
        var buttons = new List<SlackBlockKitBuilder.Button>();
        for (var i = 0; i < 9; i++) buttons.Add(SlackBlockKitBuilder.Button.Link($"B{i}", $"https://example.com/{i}"));

        var root = Parse(SlackBlockKitBuilder.Build("H", fields, null, null, buttons));

        JsonElement section = default, actions = default;
        foreach (var b in root.EnumerateArray())
        {
            if (b.GetProperty("type").GetString() == "section") section = b;
            if (b.GetProperty("type").GetString() == "actions") actions = b;
        }
        section.GetProperty("fields").GetArrayLength().Should().Be(10);
        actions.GetProperty("elements").GetArrayLength().Should().Be(5);
    }

    [Fact]
    public void Escapes_quotes_in_caller_text()
    {
        // A title containing a double quote must not break the JSON — the value round-trips.
        var json = SlackBlockKitBuilder.Build(
            "Header",
            new[] { new SlackBlockKitBuilder.Field("Task", "Fix \"login\" bug") },
            null, null, new List<SlackBlockKitBuilder.Button>());

        var fieldText = Parse(json)[1].GetProperty("fields")[0].GetProperty("text").GetString()!;
        fieldText.Should().Be("*Task*\nFix \"login\" bug");
    }

    [Fact]
    public void Action_button_carries_action_id_and_value_without_url()
    {
        var json = SlackBlockKitBuilder.Build(
            "H", SampleFields, null, null,
            new[] { SlackBlockKitBuilder.Button.Action(":mag: View details", "view_details", "task|p1|e1", "primary") });

        JsonElement actions = default;
        foreach (var b in Parse(json).EnumerateArray())
            if (b.GetProperty("type").GetString() == "actions") actions = b;

        var button = actions.GetProperty("elements")[0];
        button.GetProperty("action_id").GetString().Should().Be("view_details");
        button.GetProperty("value").GetString().Should().Be("task|p1|e1");
        button.GetProperty("style").GetString().Should().Be("primary");
        button.TryGetProperty("url", out _).Should().BeFalse();
    }

    [Fact]
    public void Truncates_an_over_long_header()
    {
        var longHeader = new string('x', 400);
        var root = Parse(SlackBlockKitBuilder.Build(
            longHeader, SampleFields, null, null, new List<SlackBlockKitBuilder.Button>()));

        var headerText = root[0].GetProperty("text").GetProperty("text").GetString()!;
        headerText.Length.Should().BeLessThanOrEqualTo(150);
        headerText.Should().EndWith("…");
    }
}
