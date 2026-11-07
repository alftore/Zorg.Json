using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Zorg.Json.Emission;

namespace Zorg.Json.AspNetCore;

/// <summary>Validates the request bodies of Minimal API endpoints.</summary>
public static class ValidationEndpointExtensions
{
    /// <summary>
    /// Validates the raw request body before parameter binding, so that every failure has a JSON Pointer.
    /// An invalid or malformed body gets a 400 from <see cref="ProblemDetailsExtensions.ToProblemDetails"/> and a body
    /// that isn't JSON gets a 415, which routing answers with no body when the handler declares a body parameter.
    /// The handler doesn't run in any of these cases.
    /// </summary>
    /// <typeparam name="T">The type of the parsed value.</typeparam>
    /// <param name="builder">The endpoint.</param>
    /// <param name="schema">The schema for the body.</param>
    /// <returns>The endpoint, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This wraps the endpoint's request delegate instead of adding an endpoint filter, because filters run after binding:
    /// the framework would already have answered a body it can't read, with an empty 400.
    /// </para>
    /// <para>
    /// The parsed value reaches a <see cref="Validated{T}"/> parameter directly. A plain body parameter is bound from the
    /// validated JSON, so it sees defaults and normalization, and an absent <see cref="Optional{T}"/> stays absent. That
    /// needs <typeparamref name="T"/> in the app's JSON options under Native AOT. The body must be UTF-8: another
    /// <c>charset</c> gets a 415. Any other handler can read the original bytes from <see cref="HttpRequest.Body"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// app.MapPost("/people", (Person person) => TypedResults.Ok(person))
    ///     .ValidateBody(PersonSchema);
    /// </code>
    /// </example>
    public static RouteHandlerBuilder ValidateBody<T>(this RouteHandlerBuilder builder, Schema<T> schema)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(schema);

        builder.Add(endpoint =>
        {
            RequestDelegate next = endpoint.RequestDelegate
                ?? throw new InvalidOperationException("ValidateBody needs an endpoint with a request delegate.");

            endpoint.RequestDelegate = context => ValidateAsync(context, schema, next);
        });

        return builder
            .WithMetadata(new EndpointSchemaMetadata(schema.Emit, SchemaDirection.Input, statusCode: null))
            .ProducesValidationProblem();
    }

    /// <summary>
    /// Documents a JSON response with a schema, for <c>Zorg.Json.AspNetCore.OpenApi</c>. It doesn't check the response.
    /// </summary>
    /// <typeparam name="T">The type of the response body.</typeparam>
    /// <param name="builder">The endpoint.</param>
    /// <param name="schema">The schema, which is emitted with <see cref="SchemaDirection.Output"/>.</param>
    /// <param name="statusCode">The status code of the response.</param>
    /// <returns>The endpoint, for chaining.</returns>
    /// <example>
    /// <code>
    /// app.MapGet("/people/{id}", GetPerson).ProducesSchema(PersonSchema);
    /// </code>
    /// </example>
    public static RouteHandlerBuilder ProducesSchema<T>(this RouteHandlerBuilder builder, Schema<T> schema, int statusCode = StatusCodes.Status200OK)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(schema);

        return builder
            .WithMetadata(new EndpointSchemaMetadata(schema.Emit, SchemaDirection.Output, statusCode))
            .Produces<T>(statusCode, "application/json");
    }

    private static async Task ValidateAsync<T>(HttpContext context, Schema<T> schema, RequestDelegate next)
    {
        HttpRequest request = context.Request;

        if (!request.HasJsonContentType())
        {
            await WriteProblemAsync(context, Problem(StatusCodes.Status415UnsupportedMediaType, "The request body must be JSON.")).ConfigureAwait(false);
            return;
        }

        if (!IsUtf8(request.ContentType))
        {
            await WriteProblemAsync(context, Problem(StatusCodes.Status415UnsupportedMediaType, "The request body must be UTF-8 JSON.")).ConfigureAwait(false);
            return;
        }

        EndpointMetadataCollection? metadata = context.GetEndpoint()?.Metadata;
        bool takesValidated = metadata?.GetMetadata<ValidatedParameterMarker>() is not null;
        bool bindsBody = !takesValidated && metadata?.GetMetadata<IAcceptsMetadata>()?.RequestType is not null;
        bool keepsRawBody = !takesValidated && !bindsBody;

        if (keepsRawBody)
        {
            request.EnableBuffering();
        }

        ParseResult<T> result;
        byte[]? validated = null;

        try
        {
            result = await schema.SafeParseAsync(
                request.Body,
                bindsBody ? element => validated = Write(element) : null,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception)
        {
            // For example a body over the size limit: 413.
            await WriteProblemAsync(context, Problem(exception.StatusCode, exception.Message)).ConfigureAwait(false);
            return;
        }

        if (!result.IsSuccess)
        {
            await WriteProblemAsync(context, result.Issues.ToProblemDetails()).ConfigureAwait(false);
            return;
        }

        context.Features.Set(new Validated<T>(result.Value));

        if (validated is not null)
        {
            ReplayValue(context.Request, validated);
        }
        else if (keepsRawBody)
        {
            request.Body.Position = 0;
        }

        await next(context).ConfigureAwait(false);
    }

    // Validation always reads UTF-8, so a charset that says otherwise would make the handler see other text.
    private static bool IsUtf8(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? parsed))
        {
            return false;
        }

        string? charset = HeaderUtilities.RemoveQuotes(parsed.Charset).Value;
        return string.IsNullOrEmpty(charset) || string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] Write(JsonElement element)
    {
        ArrayBufferWriter<byte> buffer = new();

        try
        {
            using Utf8JsonWriter writer = new(buffer);
            WriteNormalized(writer, element);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // Text that .NET strings can't hold, such as a lone surrogate: replay it as the client wrote it.
            return Encoding.UTF8.GetBytes(element.GetRawText());
        }

        return buffer.WrittenSpan.ToArray();
    }

    // 1.0 and 1e2 are integers to the schema, but not to the framework's binder.
    private static void WriteNormalized(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteNormalized(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteNormalized(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.Number:
                if (element.TryGetInt64(out long integer))
                {
                    writer.WriteNumberValue(integer);
                }
                else if (element.TryGetDecimal(out decimal number) && decimal.Truncate(number) == number && number >= long.MinValue && number <= long.MaxValue)
                {
                    writer.WriteNumberValue((long)number);
                }
                else if (WholeDigits(element) is { } digits)
                {
                    // Past long, such as 1e19 for a ulong, the binder reads only plain digits.
                    writer.WriteRawValue(digits, skipInputValidation: true);
                }
                else
                {
                    element.WriteTo(writer);
                }

                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>A whole number written with a fraction or an exponent, as plain digits, or null for any other number.</summary>
    private static string? WholeDigits(JsonElement element)
    {
        const int MaxDigits = 400;
        string text = element.GetRawText();
        int exponentAt = text.IndexOfAny(['e', 'E']);
        string mantissa = exponentAt < 0 ? text : text[..exponentAt];

        if (!int.TryParse(exponentAt < 0 ? "0" : text[(exponentAt + 1)..], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int exponent))
        {
            return null;
        }

        bool negative = mantissa.StartsWith('-');
        string unsigned = negative ? mantissa[1..] : mantissa;
        int point = unsigned.IndexOf('.', StringComparison.Ordinal);
        string digits = point < 0 ? unsigned : string.Concat(unsigned.AsSpan(0, point), unsigned.AsSpan(point + 1));
        int whole = (point < 0 ? unsigned.Length : point) + exponent;

        if (whole < 1 || whole > MaxDigits || digits[Math.Min(whole, digits.Length)..].Any(digit => digit != '0'))
        {
            return null;
        }

        string integral = (whole <= digits.Length ? digits[..whole] : digits.PadRight(whole, '0')).TrimStart('0');
        return integral.Length == 0 ? "0" : negative ? "-" + integral : integral;
    }

    // The original bytes won't do: the framework's binder would fail on input the schema accepts, such as a
    // coerced string, an absent required property that has a default, or 1.0 for an integer. The validated JSON is
    // replayed as it is, not serialized from T, so an absent Optional<T> stays absent.
    private static void ReplayValue(HttpRequest request, byte[] body)
    {
        request.Body = new MemoryStream(body, writable: false);
        request.ContentLength = body.Length;
        request.ContentType = "application/json";
    }

    private static ProblemDetails Problem(int statusCode, string detail) =>
        new() { Status = statusCode, Title = ReasonPhrases.GetReasonPhrase(statusCode), Detail = detail };

    // Not TypedResults.Problem: without AddProblemDetails it needs the problem types in the app's JSON options,
    // which under Native AOT turns every validation failure into a 500.
    private static async Task WriteProblemAsync(HttpContext context, ProblemDetails problem)
    {
        context.Response.StatusCode = problem.Status.GetValueOrDefault(StatusCodes.Status400BadRequest);

        IProblemDetailsService? service = context.RequestServices.GetService<IProblemDetailsService>();

        if (service is null || !await service.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem }).ConfigureAwait(false))
        {
            JsonTypeInfo typeInfo = ProblemJson.Default.GetTypeInfo(problem.GetType())
                ?? throw new InvalidOperationException($"{problem.GetType().Name} isn't in the problem contract.");

            await context.Response.WriteAsJsonAsync(problem, typeInfo, "application/problem+json", context.RequestAborted).ConfigureAwait(false);
        }
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(HttpValidationProblemDetails))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class ProblemJson : JsonSerializerContext;
