using System;
using System.IO;
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
using Microsoft.Extensions.Options;
using Zorg.Json.Emission;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

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
    /// parsed value, serialized with the app's JSON options, so it sees defaults and normalization. That needs
    /// <typeparamref name="T"/> in those options under Native AOT, and a round trip per request. Any other handler
    /// can read the original bytes from <see cref="HttpRequest.Body"/>.
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

        EndpointMetadataCollection? metadata = context.GetEndpoint()?.Metadata;
        bool takesValidated = metadata?.GetMetadata<ValidatedParameterMarker>() is not null;
        bool bindsBody = !takesValidated && metadata?.GetMetadata<IAcceptsMetadata>()?.RequestType is not null;
        bool keepsRawBody = !takesValidated && !bindsBody;

        if (keepsRawBody)
        {
            request.EnableBuffering();
        }

        ParseResult<T> result;

        try
        {
            result = await schema.SafeParseAsync(request.Body, context.RequestAborted).ConfigureAwait(false);
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

        if (bindsBody)
        {
            ReplayValue(context, result.Value);
        }
        else if (keepsRawBody)
        {
            request.Body.Position = 0;
        }

        await next(context).ConfigureAwait(false);
    }

    // The original bytes won't do: the framework's binder would fail on input the schema accepts, such as a
    // coerced string, an absent required property that has a default, or 1.0 for an integer.
    private static void ReplayValue<T>(HttpContext context, T value)
    {
        JsonSerializerOptions options = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)));

        context.Request.Body = new MemoryStream(body, writable: false);
        context.Request.ContentLength = body.Length;
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
