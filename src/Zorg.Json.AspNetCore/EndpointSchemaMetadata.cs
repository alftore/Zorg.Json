using System;
using Zorg.Json.Emission;

namespace Zorg.Json.AspNetCore;

/// <summary>
/// Endpoint metadata naming the schema of a request or response body. <c>ValidateBody</c> and <c>ProducesSchema</c>
/// add it, and <c>Zorg.Json.AspNetCore.OpenApi</c> reads it.
/// </summary>
/// <example>
/// <code>
/// EndpointSchemaMetadata? metadata = endpoint.Metadata.GetMetadata&lt;EndpointSchemaMetadata&gt;();
/// </code>
/// </example>
public sealed class EndpointSchemaMetadata
{
    private readonly Func<JsonSchemaOptions?, EmitResult> _emit;

    internal EndpointSchemaMetadata(Func<JsonSchemaOptions?, EmitResult> emit, SchemaDirection direction, int? statusCode)
    {
        _emit = emit;
        Direction = direction;
        StatusCode = statusCode;
    }

    /// <summary><see cref="SchemaDirection.Input"/> for a request body, <see cref="SchemaDirection.Output"/> for a response.</summary>
    public SchemaDirection Direction { get; }

    /// <summary>The status code of a response. Null for a request body.</summary>
    public int? StatusCode { get; }

    /// <summary>Emits the schema.</summary>
    /// <param name="options">Target and policy. The direction should be <see cref="Direction"/>.</param>
    /// <returns>The schema and its diagnostics.</returns>
    /// <example>
    /// <code>
    /// EmitResult result = metadata.Emit(new JsonSchemaOptions { Direction = metadata.Direction });
    /// </code>
    /// </example>
    public EmitResult Emit(JsonSchemaOptions options) => _emit(options);
}
