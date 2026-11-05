using System.Collections.Immutable;
using System.Text.Json;
using Zorg.Json.Validation;

namespace Zorg.Json.Sampling;

/// <summary>A schema with a .NET result, which a sample also has to convert to.</summary>
internal interface ISampleTarget
{
    /// <summary>Finishes a validation as parsing does, including the conversion.</summary>
    public ImmutableArray<ParseIssue> FinishSample(JsonElement value, ValidationContext context);
}
