using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace Zorg.Json.Emission;

/// <summary>The outcome of <see cref="Schema{T}.Emit"/>.</summary>
/// <param name="Schema">The JSON Schema document.</param>
/// <param name="Diagnostics">Everything the document couldn't express.</param>
public sealed record EmitResult(JsonObject Schema, ImmutableArray<EmitDiagnostic> Diagnostics);

/// <summary>A rule that an emitted schema leaves out.</summary>
/// <param name="Keyword">The JSON Schema keyword left out. Null for a refinement.</param>
/// <param name="Message">What was left out and why.</param>
public sealed record EmitDiagnostic(string? Keyword, string Message);
