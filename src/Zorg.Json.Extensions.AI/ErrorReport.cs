using System;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zorg.Json.Extensions.AI;

/// <summary>The error a model gets back for a call it got wrong.</summary>
internal static class ErrorReport
{
    private const int MaxIssues = 20;

    public static JsonElement Create(string message, ImmutableArray<ParseIssue> issues)
    {
        JsonArray list = [];

        foreach (ParseIssue issue in issues.AsSpan()[..Math.Min(issues.Length, MaxIssues)])
        {
            list.Add((JsonNode)new JsonObject
            {
                ["path"] = Clean(issue.InstanceLocation),
                ["code"] = Clean(issue.Code),
                ["message"] = Clean(issue.Message),
            });
        }

        JsonObject report = new()
        {
            ["error"] = "invalid_arguments",
            ["message"] = Clean(message),
            ["issues"] = list,
        };

        if (issues.Length > MaxIssues)
        {
            report["omitted"] = issues.Length - MaxIssues;
        }

        return JsonElement.Parse(report.ToJsonString());
    }

    // Names and values from the model can hold a lone surrogate, which no JSON writer will encode.
    // The report is serialized again by the chat client, so it must hold text that can be.
    public static string Clean(string text)
    {
        StringBuilder? builder = null;

        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            bool paired = char.IsHighSurrogate(current) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]);

            if (paired)
            {
                builder?.Append(current).Append(text[index + 1]);
                index++;
            }
            else if (char.IsSurrogate(current))
            {
                builder ??= new StringBuilder(text.Length).Append(text, 0, index);
                builder.Append('�');
            }
            else
            {
                builder?.Append(current);
            }
        }

        return builder?.ToString() ?? text;
    }
}
