using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Zorg.Json.SourceGeneration;

/// <summary>What the generator reports. Each one stops a mistake from turning into a schema that says less than the code seems to.</summary>
internal static class Descriptors
{
    private const string Category = "Zorg.Json";

    public static readonly DiagnosticDescriptor NotPartial = new(
        "ZORG0001", "The type must be partial", "'{0}' needs the partial modifier to get a generated schema", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor Unsupported = new(
        "ZORG0002", "The type can't get a generated schema", "'{0}' can't get a generated schema, because a generic or nested type isn't supported", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NameTaken = new(
        "ZORG0003", "The type already has a member named Schema", "'{0}' already has a member named Schema", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor BadContext = new(
        "ZORG0004", "The context doesn't know the type", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotApplied = new(
        "ZORG0005", "An attribute can't be stated in the schema", "{0}", Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotRequired = new(
        "ZORG0006", "[Required] doesn't make a property required", "'{0}' has [Required], but the contract decides what must be present and may be null: make it required and not nullable", Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CustomShape = new(
        "ZORG0007", "The JSON of the type isn't what its properties say", "{0}. Write the schema by hand.", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static ImmutableArray<DiagnosticDescriptor> All { get; } = [NotPartial, Unsupported, NameTaken, BadContext, NotApplied, NotRequired, CustomShape];
}
