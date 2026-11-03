using System;

namespace Zorg.Json.Import;

/// <summary>
/// Thrown by <see cref="Z.FromJsonSchema(System.Text.Json.JsonElement, JsonSchemaImportOptions?)"/> when a document
/// can't be imported faithfully. Zod equivalent: the error <c>z.fromJSONSchema</c> throws.
/// </summary>
/// <example>
/// <code>
/// try
/// {
///     JsonValueSchema schema = Z.FromJsonSchema(text);
/// }
/// catch (JsonSchemaImportException exception) when (exception.Code == JsonSchemaImportCodes.UnresolvedRef)
/// {
///     Console.WriteLine($"{exception.SchemaLocation}: {exception.Message}");
/// }
/// </code>
/// </example>
public sealed class JsonSchemaImportException : Exception
{
    /// <summary>Creates the exception with no code.</summary>
    public JsonSchemaImportException()
    {
    }

    /// <summary>Creates the exception with no code.</summary>
    /// <param name="message">The exception message.</param>
    public JsonSchemaImportException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with no code.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The cause.</param>
    public JsonSchemaImportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a location in the document.</summary>
    /// <param name="code">A value from <see cref="JsonSchemaImportCodes"/>.</param>
    /// <param name="schemaLocation">A JSON Pointer into the document.</param>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The cause.</param>
    public JsonSchemaImportException(string code, string schemaLocation, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        SchemaLocation = schemaLocation;
    }

    /// <summary>The stable reason, a value from <see cref="JsonSchemaImportCodes"/>.</summary>
    public string Code { get; } = string.Empty;

    /// <summary>A JSON Pointer to the keyword at fault. Empty for the document itself.</summary>
    public string SchemaLocation { get; } = string.Empty;
}
