using System.Collections.Immutable;

namespace Zorg.Json;

/// <summary>One reason why a JSON document failed validation.</summary>
public sealed record ParseIssue
{
    /// <summary>The stable, machine-readable reason. See <see cref="IssueCodes"/>.</summary>
    public required string Code { get; init; }

    /// <summary>The message for people, localized from <see cref="Code"/>.</summary>
    public required string Message { get; init; }

    /// <summary>A JSON Pointer to the failing value. Empty for the document root.</summary>
    public required string InstanceLocation { get; init; }

    /// <summary>The JSON Schema keyword that failed, such as <c>minLength</c>. Null when no keyword applies.</summary>
    public string? Keyword { get; init; }

    /// <summary>Details specific to <see cref="Code"/>, such as <c>minimum</c> or <c>expected</c>.</summary>
    public ImmutableDictionary<string, object?> Parameters { get; init; } = ImmutableDictionary<string, object?>.Empty;
}
