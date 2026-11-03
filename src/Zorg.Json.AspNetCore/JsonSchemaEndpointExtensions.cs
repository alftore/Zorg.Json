using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using Zorg.Json.Emission;

namespace Zorg.Json.AspNetCore;

/// <summary>Serves schemas as JSON Schema documents.</summary>
public static class JsonSchemaEndpointExtensions
{
    /// <summary>The media type of a served schema.</summary>
    public const string SchemaContentType = "application/schema+json";

    /// <summary>
    /// Serves a schema with GET and HEAD as <c>application/schema+json</c>. It's emitted once, here, so a schema that
    /// can't be emitted fails at startup. Responses have an ETag, answer <c>If-None-Match</c> with a 304, and
    /// are <c>Cache-Control: no-cache</c>, so that clients revalidate instead of keeping an old version.
    /// </summary>
    /// <typeparam name="T">The type of the parsed value.</typeparam>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="pattern">The route.</param>
    /// <param name="schema">The schema to serve.</param>
    /// <param name="options">Target, direction and policy. Null uses the defaults: Draft 2020-12, describing the input.</param>
    /// <returns>The endpoint, for conventions such as <c>RequireCors</c>.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> sets <see cref="JsonSchemaOptions.ReferencePrefix"/> and the schema has definitions,
    /// which would leave the document with references it doesn't contain.
    /// </exception>
    /// <example>
    /// <code>
    /// app.MapJsonSchema("/schemas/person.json", PersonSchema);
    /// app.MapJsonSchema("/schemas/person.draft-07.json", PersonSchema, new() { Target = JsonSchemaProfile.Draft07 });
    /// </code>
    /// </example>
    public static IEndpointConventionBuilder MapJsonSchema<T>(
        this IEndpointRouteBuilder endpoints,
        [StringSyntax("Route")] string pattern,
        Schema<T> schema,
        JsonSchemaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(schema);

        EmitResult emitted = schema.Emit(options);

        if (!emitted.Definitions.IsEmpty)
        {
            throw new ArgumentException("A served document has to contain its definitions, so ReferencePrefix can't be set.", nameof(options));
        }

        ArrayBufferWriter<byte> buffer = new();

        // Relaxed escaping keeps a pattern such as "^\+" readable. The media type isn't one a browser renders as HTML.
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            emitted.Schema.WriteTo(writer);
        }

        return endpoints.MapMethods(pattern, [HttpMethods.Get, HttpMethods.Head], new SchemaDocument(buffer.WrittenSpan.ToArray()).WriteAsync);
    }

    private sealed class SchemaDocument(byte[] content)
    {
        private readonly EntityTagHeaderValue _etag = new($"\"{Convert.ToHexStringLower(SHA256.HashData(content).AsSpan(0, 16))}\"");

        public Task WriteAsync(HttpContext context)
        {
            HttpResponse response = context.Response;
            response.Headers.ETag = _etag.ToString();
            response.Headers.CacheControl = "no-cache";

            if (IsCurrent(context.Request))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                return Task.CompletedTask;
            }

            response.ContentType = SchemaContentType;
            response.ContentLength = content.Length;

            return HttpMethods.IsHead(context.Request.Method)
                ? Task.CompletedTask
                : response.BodyWriter.WriteAsync(content, context.RequestAborted).AsTask();
        }

        // If-None-Match uses weak comparison (RFC 9110, 13.1.2), so W/"x" matches "x".
        private bool IsCurrent(HttpRequest request) =>
            request.GetTypedHeaders().IfNoneMatch.Any(tag =>
                tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(_etag, useStrongComparison: false));
    }
}
