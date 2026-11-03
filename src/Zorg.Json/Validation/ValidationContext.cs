using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json;

namespace Zorg.Json.Validation;

internal sealed class ValidationContext
{
    private List<ParseIssue>? _issues;

    public bool HasIssues => _issues is not null;

    public ImmutableArray<ParseIssue> ToIssues() => _issues is null ? [] : [.. _issues];

    public void Report(string code, string keyword, string messageKey, string parameter, object? value, params object?[] messageArguments) =>
        Add(new ParseIssue
        {
            Code = code,
            Message = Messages.Get(messageKey, messageArguments),
            InstanceLocation = string.Empty,
            Keyword = keyword,
            Parameters = ImmutableDictionary<string, object?>.Empty.Add(parameter, value),
        });

    public void ReportCustom(string message) =>
        Add(new ParseIssue { Code = IssueCodes.Custom, Message = message, InstanceLocation = string.Empty });

    public void ReportInvalidJson(JsonException exception) =>
        Add(new ParseIssue
        {
            Code = IssueCodes.InvalidJson,
            Message = Messages.Get(IssueCodes.InvalidJson, exception.Message),
            InstanceLocation = string.Empty,
        });

    private void Add(ParseIssue issue) => (_issues ??= []).Add(issue);
}
