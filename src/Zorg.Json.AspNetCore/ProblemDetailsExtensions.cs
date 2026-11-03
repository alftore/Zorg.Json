using System.Buffers;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Zorg.Json.AspNetCore;

/// <summary>Converts parse issues to RFC 9457 problem details.</summary>
public static class ProblemDetailsExtensions
{
    /// <summary>The extension member that holds the <see cref="ParseIssue.Code"/> values.</summary>
    public const string CodesExtension = "codes";

    /// <summary>
    /// Creates a 400 validation problem. <c>errors</c> holds the messages by JSON Pointer, and the <c>codes</c>
    /// extension member holds the stable codes in the same shape and order, as a <see cref="JsonElement"/>.
    /// </summary>
    /// <param name="issues">The issues of a failed parse.</param>
    /// <returns>
    /// The problem. It's the <see cref="HttpValidationProblemDetails"/> base of MVC's <c>ValidationProblemDetails</c>,
    /// which has the same JSON and, unlike the MVC type, is serializable under Native AOT.
    /// </returns>
    /// <example>
    /// <code>
    /// ParseResult&lt;string&gt; result = Z.String().MinLength(3).SafeParse("\"a\"");
    /// IResult response = TypedResults.Problem(result.Issues.ToProblemDetails());
    /// // { "status": 400, "errors": { "": ["..."] }, "codes": { "": ["too_small"] } }
    /// </code>
    /// </example>
    public static HttpValidationProblemDetails ToProblemDetails(this ImmutableArray<ParseIssue> issues)
    {
        HttpValidationProblemDetails details = new(issues.ToDictionary())
        {
            Status = StatusCodes.Status400BadRequest,
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        };

        details.Extensions[CodesExtension] = Codes(issues);
        return details;
    }

    // A JsonElement, because the framework's AOT-safe problem details serializer can't write arbitrary collections.
    private static JsonElement Codes(ImmutableArray<ParseIssue> issues)
    {
        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();

            foreach (IGrouping<string, ParseIssue> group in issues.GroupBy(issue => issue.InstanceLocation))
            {
                writer.WriteStartArray(group.Key);

                foreach (ParseIssue issue in group)
                {
                    writer.WriteStringValue(issue.Code);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return JsonElement.Parse(buffer.WrittenSpan);
    }
}
