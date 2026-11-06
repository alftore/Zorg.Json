using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zorg.Json.SourceGeneration;

/// <summary>Reads a type and its validation attributes into a <see cref="TypeModel"/>, as ordinary fluent calls.</summary>
internal static class TypeReader
{
    private const string Annotations = "System.ComponentModel.DataAnnotations.";
    private const string Attribute = "Zorg.Json.GenerateSchemaAttribute";

    public static string AttributeName => Attribute;

    public static TypeModel Read(GeneratorAttributeSyntaxContext context, CancellationToken cancellation)
    {
        INamedTypeSymbol type = (INamedTypeSymbol)context.TargetSymbol;
        Location? location = type.Locations.FirstOrDefault();
        List<Finding> findings = [];
        bool canGenerate = true;

        void Stop(DiagnosticDescriptor descriptor, string argument)
        {
            findings.Add(Finding.At(descriptor, argument, location));
            canGenerate = false;
        }

        if (type.TypeParameters.Length > 0 || type.ContainingType is not null)
        {
            Stop(Descriptors.Unsupported, type.Name);
        }

        if (!type.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax(cancellation) is TypeDeclarationSyntax syntax && syntax.Modifiers.Any(SyntaxKind.PartialKeyword)))
        {
            Stop(Descriptors.NotPartial, type.Name);
        }

        if (type.GetMembers("Schema").Any(member => !member.IsImplicitlyDeclared))
        {
            Stop(Descriptors.NameTaken, type.Name);
        }

        string fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string contextName = ReadContext(context.Attributes[0], type, Stop);
        List<PropertyModel> properties = [];

        foreach (IPropertySymbol property in AllProperties(type))
        {
            cancellation.ThrowIfCancellationRequested();

            if (ReadProperty(property, type, findings) is { } model)
            {
                properties.Add(model);
            }
        }

        string typeCalls = Description(type.GetAttributes()) is { } text ? $".Description({Literal(text)})" : string.Empty;
        string? space = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString();

        return new TypeModel(
            fullName,
            fullName.Replace("global::", string.Empty).Replace('<', '_').Replace('>', '_'),
            space,
            Declaration(type),
            type.Name,
            contextName,
            typeCalls,
            new EquatableArray<PropertyModel>([.. properties]),
            new EquatableArray<Finding>([.. findings]),
            canGenerate);
    }

    private static string Declaration(INamedTypeSymbol type) => (type.IsRecord, type.TypeKind) switch
    {
        (true, TypeKind.Struct) => "partial record struct",
        (true, _) => "partial record",
        (_, TypeKind.Struct) => "partial struct",
        _ => "partial class",
    };

    private static string ReadContext(AttributeData attribute, INamedTypeSymbol type, Action<DiagnosticDescriptor, string> stop)
    {
        if (attribute.ConstructorArguments is not [{ Value: INamedTypeSymbol context }])
        {
            stop(Descriptors.BadContext, "[GenerateSchema] needs a JsonSerializerContext");
            return string.Empty;
        }

        bool isContext = false;

        for (INamedTypeSymbol? current = context; current is not null; current = current.BaseType)
        {
            isContext |= current.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializerContext";
        }

        if (!isContext)
        {
            stop(Descriptors.BadContext, $"'{context.Name}' isn't a JsonSerializerContext");
        }
        else if (!context.GetAttributes().Any(candidate => candidate.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializableAttribute"
            && candidate.ConstructorArguments is [{ Value: ITypeSymbol listed }] && SymbolEqualityComparer.Default.Equals(listed, type)))
        {
            stop(Descriptors.BadContext, $"'{context.Name}' has no [JsonSerializable(typeof({type.Name}))]");
        }

        return context.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    /// <summary>The public instance properties, the most derived first, as the serializer sees them.</summary>
    private static IEnumerable<IPropertySymbol> AllProperties(INamedTypeSymbol type)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (INamedTypeSymbol? current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (IPropertySymbol property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property is { DeclaredAccessibility: Accessibility.Public, IsStatic: false, IsIndexer: false, IsImplicitlyDeclared: false } && seen.Add(property.Name))
                {
                    yield return property;
                }
            }
        }
    }

    /// <summary>Attributes of the property, and of the constructor parameter of a record, which is where they go without <c>property:</c>.</summary>
    private static ImmutableArray<AttributeData> AttributesOf(IPropertySymbol property, INamedTypeSymbol type)
    {
        ImmutableArray<AttributeData> own = property.GetAttributes();

        foreach (IMethodSymbol constructor in type.InstanceConstructors)
        {
            if (constructor.Parameters.FirstOrDefault(parameter => parameter.Name == property.Name) is { } match
                && constructor.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is RecordDeclarationSyntax))
            {
                return own.AddRange(match.GetAttributes());
            }
        }

        return own;
    }

    private static bool IsIgnored(ImmutableArray<AttributeData> attributes) => attributes.Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonExtensionDataAttribute"
        || (attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonIgnoreAttribute"
            && !attribute.NamedArguments.Any(argument => argument.Key == "Condition" && argument.Value.Value is int and not 1)));

    private static string? Description(ImmutableArray<AttributeData> attributes) =>
        attributes.FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.DescriptionAttribute")?.ConstructorArguments is [{ Value: string text }]
            ? text
            : null;

    private static string Literal(string text) => SymbolDisplay.FormatLiteral(text, quote: true);

    private static PropertyModel? ReadProperty(IPropertySymbol property, INamedTypeSymbol owner, List<Finding> findings)
    {
        ImmutableArray<AttributeData> attributes = AttributesOf(property, owner);

        if (IsIgnored(attributes))
        {
            return null;
        }

        ITypeSymbol type = Unwrap(property.Type);
        ITypeSymbol? item = ItemOf(type);
        ITypeSymbol target = item ?? type;
        string? nested = HasGeneratedSchema(target) ? target.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : null;
        string kind = KindOf(type, item);
        List<string> calls = [];

        foreach (AttributeData attribute in attributes)
        {
            ReadAttribute(attribute, property, owner, type, kind, calls, findings);
        }

        if (calls.Count == 0 && nested is null)
        {
            return null;
        }

        return new PropertyModel(property.Name, string.Concat(calls), nested, item is not null, kind);
    }

    private static string KindOf(ITypeSymbol type, ITypeSymbol? item)
    {
        if (type.SpecialType == SpecialType.System_String)
        {
            return "string";
        }

        if (type.SpecialType == SpecialType.System_Boolean)
        {
            return "boolean";
        }

        if (IsNumber(type))
        {
            return "number";
        }

        return item is not null ? "array" : "other";
    }

    /// <summary>The type under <c>T?</c> and <c>Optional&lt;T&gt;</c>.</summary>
    private static ITypeSymbol Unwrap(ITypeSymbol type)
    {
        while (type is INamedTypeSymbol { IsGenericType: true, TypeArguments: [var argument] } named
            && (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T || named.OriginalDefinition.ToDisplayString() == "Zorg.Json.Optional<T>"))
        {
            type = argument;
        }

        return type;
    }

    private static bool IsNumber(ITypeSymbol type) => type.SpecialType is SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16
        or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
        or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal;

    /// <summary>The item type of a collection that is written as an array. Null for text and for dictionaries.</summary>
    private static ITypeSymbol? ItemOf(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String)
        {
            return null;
        }

        if (type is IArrayTypeSymbol array)
        {
            return array.ElementType;
        }

        if (type.AllInterfaces.Concat(type is INamedTypeSymbol self ? [self] : Array.Empty<INamedTypeSymbol>())
            .Any(candidate => candidate.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.IDictionary<TKey, TValue>" or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"))
        {
            return null;
        }

        return type.AllInterfaces.Concat(type is INamedTypeSymbol named ? [named] : Array.Empty<INamedTypeSymbol>())
            .FirstOrDefault(candidate => candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)?.TypeArguments[0];
    }

    private static bool HasGeneratedSchema(ITypeSymbol type) =>
        type.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == Attribute);

    private static void ReadAttribute(AttributeData attribute, IPropertySymbol property, INamedTypeSymbol owner, ITypeSymbol type, string kind, List<string> calls, List<Finding> findings)
    {
        string? name = attribute.AttributeClass?.ToDisplayString();
        Location? location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? property.Locations.FirstOrDefault();

        void Skip() => findings.Add(Finding.At(
            Descriptors.NotApplied, $"[{attribute.AttributeClass?.Name}] on '{property.Name}' can't be stated in the schema, so it isn't checked by Zorg.Json", location));

        bool IsAnnotation(string candidate) => name == Annotations + candidate;
        int? First() => attribute.ConstructorArguments is [{ Value: int value }, ..] ? value : null;
        object? Named(string key) => attribute.NamedArguments.FirstOrDefault(argument => argument.Key == key).Value.Value;

        void Length(int? minimum, int? maximum)
        {
            if (kind == "string")
            {
                calls.Add(minimum is { } low ? $".MinLength({low})" : string.Empty);
                calls.Add(maximum is { } high ? $".MaxLength({high})" : string.Empty);
            }
            else if (kind == "array")
            {
                calls.Add(minimum is { } low ? $".MinItems({low})" : string.Empty);
                calls.Add(maximum is { } high ? $".MaxItems({high})" : string.Empty);
            }
            else
            {
                Skip();
            }
        }

        void Text(string call)
        {
            if (kind == "string")
            {
                calls.Add(call);
            }
            else
            {
                Skip();
            }
        }

        if (name == "System.ComponentModel.DescriptionAttribute")
        {
            if (kind == "other")
            {
                Skip();
            }
            else if (attribute.ConstructorArguments is [{ Value: string text }])
            {
                calls.Add($".Description({Literal(text)})");
            }
        }
        else if (IsAnnotation("MinLengthAttribute"))
        {
            Length(First(), null);
        }
        else if (IsAnnotation("MaxLengthAttribute"))
        {
            // The constructor without a length means the largest the store allows, which says nothing here.
            Length(null, First());
        }
        else if (IsAnnotation("LengthAttribute"))
        {
            Length(First(), attribute.ConstructorArguments is [_, { Value: int maximum }] ? maximum : null);
        }
        else if (IsAnnotation("StringLengthAttribute"))
        {
            int minimum = Named("MinimumLength") is int given ? given : 0;
            Length(minimum > 0 ? minimum : null, First());
        }
        else if (IsAnnotation("RegularExpressionAttribute"))
        {
            if (attribute.ConstructorArguments is [{ Value: string pattern }])
            {
                Text($".Pattern({Literal(pattern)})");
            }
        }
        else if (IsAnnotation("EmailAddressAttribute"))
        {
            Text(".Email()");
        }
        else if (IsAnnotation("UrlAttribute"))
        {
            Text(".Uri()");
        }
        else if (IsAnnotation("RangeAttribute"))
        {
            ReadRange(attribute, type, kind, calls, Skip);
        }
        else if (IsAnnotation("RequiredAttribute"))
        {
            if (kind == "string" && Named("AllowEmptyStrings") is not true)
            {
                calls.Add(".MinLength(1)");
            }

            if (!property.IsRequired && !IsConstructorParameter(property, owner))
            {
                findings.Add(Finding.At(Descriptors.NotRequired, property.Name, location));
            }
        }
        else if (attribute.AttributeClass is { } candidate && IsValidation(candidate))
        {
            Skip();
        }
    }

    private static bool IsConstructorParameter(IPropertySymbol property, INamedTypeSymbol owner) => owner.InstanceConstructors.Any(constructor =>
        !constructor.IsImplicitlyDeclared && constructor.Parameters.Any(parameter => string.Equals(parameter.Name, property.Name, StringComparison.OrdinalIgnoreCase)));

    private static bool IsValidation(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == Annotations + "ValidationAttribute")
            {
                return true;
            }
        }

        return false;
    }

    private static void ReadRange(AttributeData attribute, ITypeSymbol type, string kind, List<string> calls, Action skip)
    {
        if (kind != "number" || attribute.ConstructorArguments is not [{ Value: var low and (int or double) }, { Value: var high and (int or double) }])
        {
            skip();
            return;
        }

        bool Flag(string key) => attribute.NamedArguments.FirstOrDefault(argument => argument.Key == key).Value.Value is true;
        double minimum = Convert.ToDouble(low, CultureInfo.InvariantCulture);
        double maximum = Convert.ToDouble(high, CultureInfo.InvariantCulture);

        if (Number(type, low, minimum) is { } lower)
        {
            calls.Add($".{(Flag("MinimumIsExclusive") ? "ExclusiveMinimum" : "Minimum")}({lower})");
        }

        if (Number(type, high, maximum) is { } upper)
        {
            calls.Add($".{(Flag("MaximumIsExclusive") ? "ExclusiveMaximum" : "Maximum")}({upper})");
        }
    }

    /// <summary>A constant of the property's type. Null for a bound the type can't hold, which limits nothing that fits in it.</summary>
    private static string? Number(ITypeSymbol type, object raw, double value)
    {
        (double smallest, double largest) = type.SpecialType switch
        {
            SpecialType.System_Byte => (byte.MinValue, byte.MaxValue),
            SpecialType.System_SByte => (sbyte.MinValue, sbyte.MaxValue),
            SpecialType.System_Int16 => (short.MinValue, short.MaxValue),
            SpecialType.System_UInt16 => (ushort.MinValue, ushort.MaxValue),
            SpecialType.System_Int32 => (int.MinValue, int.MaxValue),
            SpecialType.System_UInt32 => (uint.MinValue, uint.MaxValue),
            SpecialType.System_Int64 => (long.MinValue, long.MaxValue),
            SpecialType.System_UInt64 => (ulong.MinValue, ulong.MaxValue),
            SpecialType.System_Single => (float.MinValue, float.MaxValue),
            SpecialType.System_Decimal => ((double)decimal.MinValue, (double)decimal.MaxValue),
            _ => (double.MinValue, double.MaxValue),
        };

        if (double.IsInfinity(value) || double.IsNaN(value) || value <= smallest || value >= largest)
        {
            return null;
        }

        string text = raw is int whole ? whole.ToString(CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture) + "d";
        return $"({type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){text}";
    }
}
