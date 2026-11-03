using Zorg.Json.Model;

namespace Zorg.Json;

/// <summary>Any schema, whatever it parses to. Lets different schema types sit side by side, as object properties do.</summary>
public interface ISchema
{
    /// <summary>Gets the keyword model behind this schema.</summary>
    /// <returns>The node.</returns>
    public SchemaNode ToNode();
}
