using System.Text.Json.Nodes;
using Zorg.Json.Model;

namespace Zorg.Json.Emission;

/// <summary>
/// Changes the model before emission, for example to close every object. Runs on each node, children first,
/// including <c>$ref</c> targets. Use it in a <see cref="JsonSchemaProfile"/>.
/// </summary>
public interface ISchemaRewriter
{
    /// <summary>Rewrites one node.</summary>
    /// <param name="node">The node, whose children are already rewritten.</param>
    /// <param name="context">The emission in progress.</param>
    /// <returns>The node to emit. Return <paramref name="node"/> when nothing applies.</returns>
    public SchemaNode Rewrite(SchemaNode node, RewriteContext context);
}

/// <summary>
/// Changes the emitted JSON, as a final step for spelling the model can't express.
/// Use it in a <see cref="JsonSchemaProfile"/>.
/// </summary>
public interface IJsonSchemaPostProcessor
{
    /// <summary>Changes the root schema in place.</summary>
    /// <param name="schema">The emitted schema.</param>
    /// <param name="context">The emission in progress.</param>
    public void Process(JsonObject schema, EmitContext context);
}

/// <summary>What an <see cref="ISchemaRewriter"/> can see of the emission in progress.</summary>
/// <param name="Options">The emit options.</param>
/// <param name="IsRoot">Whether this is the root node.</param>
/// <param name="Report">Records a diagnostic.</param>
public sealed record RewriteContext(JsonSchemaOptions Options, bool IsRoot, System.Action<EmitDiagnostic> Report);

/// <summary>What an <see cref="IJsonSchemaPostProcessor"/> can see of the emission in progress.</summary>
/// <param name="Options">The emit options.</param>
/// <param name="Report">Records a diagnostic.</param>
public sealed record EmitContext(JsonSchemaOptions Options, System.Action<EmitDiagnostic> Report);
