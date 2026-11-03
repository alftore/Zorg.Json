using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Zorg.Json.AspNetCore;

/// <summary>
/// A handler parameter that receives the value <see cref="ValidationEndpointExtensions.ValidateBody"/> parsed. Prefer it
/// over a plain <typeparamref name="T"/> parameter: the body is parsed once, and <typeparamref name="T"/> needs no
/// metadata in the app's JSON options.
/// </summary>
/// <typeparam name="T">The type of the parsed value.</typeparam>
/// <example>
/// <code>
/// app.MapPost("/people", (Validated&lt;Person&gt; person) => TypedResults.Ok(person.Value))
///     .ValidateBody(PersonSchema);
/// </code>
/// </example>
public sealed class Validated<T> : IBindableFromHttpContext<Validated<T>>, IEndpointParameterMetadataProvider
{
    internal Validated(T value) => Value = value;

    /// <summary>The validated value.</summary>
    public T Value { get; }

    /// <inheritdoc/>
    static ValueTask<Validated<T>?> IBindableFromHttpContext<Validated<T>>.BindAsync(HttpContext context, ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameter);

        Validated<T> validated = context.Features.Get<Validated<T>>() ?? throw new InvalidOperationException(
            $"Parameter '{parameter.Name}' needs ValidateBody on its endpoint, with a schema that parses to {typeof(T).Name}.");

        return ValueTask.FromResult<Validated<T>?>(validated);
    }

    /// <inheritdoc/>
    static void IEndpointParameterMetadataProvider.PopulateMetadata(ParameterInfo parameter, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Metadata.Add(new AcceptsMetadata(["application/json"], typeof(T)));
        builder.Metadata.Add(ValidatedParameterMarker.Instance);
    }
}

/// <summary>Tells the body validation that the handler takes <see cref="Validated{T}"/>, so nothing reads the body after it.</summary>
internal sealed class ValidatedParameterMarker
{
    public static readonly ValidatedParameterMarker Instance = new();
}
