using System;
using System.Collections.Immutable;

namespace Zorg.Json;

/// <summary>Thrown by <see cref="Schema{T}.Parse(string)"/> when the input is invalid. Zod equivalent: <c>ZodError</c>.</summary>
public sealed class SchemaValidationException : Exception
{
    /// <summary>Creates the exception with no issues.</summary>
    public SchemaValidationException()
    {
    }

    /// <summary>Creates the exception with no issues.</summary>
    /// <param name="message">The exception message.</param>
    public SchemaValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with no issues.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The cause.</param>
    public SchemaValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception from the issues of a failed parse.</summary>
    /// <param name="issues">The reasons for failure.</param>
    public SchemaValidationException(ImmutableArray<ParseIssue> issues)
        : base(issues.ToPrettyString()) => Issues = issues;

    /// <summary>The reasons for failure.</summary>
    public ImmutableArray<ParseIssue> Issues { get; } = [];
}
