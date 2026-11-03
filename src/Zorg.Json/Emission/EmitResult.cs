using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace Zorg.Json.Emission;

/// <summary>The outcome of <see cref="Schema{T}.Emit"/>.</summary>
/// <param name="Schema">The JSON Schema document.</param>
/// <param name="Diagnostics">Everything the document couldn't express.</param>
public sealed record EmitResult(JsonObject Schema, ImmutableArray<EmitDiagnostic> Diagnostics)
{
    /// <summary>Definitions kept out of <see cref="Schema"/>, when <see cref="JsonSchemaOptions.ReferencePrefix"/> is set.</summary>
    public ImmutableDictionary<string, JsonObject> Definitions { get; init; } = ImmutableDictionary<string, JsonObject>.Empty;
}

/// <summary>A rule that an emitted schema leaves out.</summary>
/// <param name="Keyword">The JSON Schema keyword left out. Null for a refinement.</param>
/// <param name="Message">What was left out and why.</param>
/// <param name="Location">A JSON Pointer into the schema. Empty for the root.</param>
public sealed record EmitDiagnostic(string? Keyword, string Message, string Location = "");
