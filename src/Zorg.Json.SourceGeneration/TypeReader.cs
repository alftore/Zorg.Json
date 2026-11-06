using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zorg.Json.SourceGeneration;

/// <summary>
/// Reads a type and its validation attributes into a <see cref="TypeModel"/>, as ordinary fluent calls. It states what an attribute
/// means at runtime or reports that it can't: a rule that silently means something else is worse than none.
/// </summary>
internal static class TypeReader
{
    private const string Annotations = "System.ComponentModel.DataAnnotations.";
    private const string Serialization = "System.Text.Json.Serialization.";
    private const string Attribute = "Zorg.Json.GenerateSchemaAttribute";

    public static string AttributeName => Attribute;

    private static string? NameOf(AttributeData attribute) => attribute.AttributeClass?.ToDisplayString();

    private static bool Has(ImmutableArray<AttributeData> attributes, string name) => attributes.Any(attribute => NameOf(attribute) == name);

    public static TypeModel Read(GeneratorAttributeSyntaxContext context, CancellationToken cancellation)
    {
        INamedTypeSymbol type = (INamedTypeSymbol)context.TargetSymbol;
        Location? location = type.Locations.FirstOrDefault();
        List<Finding> findings = [];
        bool canGenerate = true;

        void Stop(DiagnosticDescriptor descriptor, string argument)
        {
            findings.Add(new Finding(descriptor.Id, argument, location));
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

        if (type.Name == "Schema" || type.GetMembers("Schema").Any(member => !member.IsImplicitlyDeclared))
        {
            Stop(Descriptors.NameTaken, type.Name);
        }

        if (OwnShape(type) is { } reason)
        {
            Stop(Descriptors.CustomShape, $"'{type.Name}' {reason}, so its JSON isn't what its properties say");
        }

        string fullName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        string contextName = ReadContext(context.Attributes[0], type, Stop);
        List<PropertyModel> properties = [];

        foreach (AttributeData attribute in type.GetAttributes())
        {
            if (IsValidation(attribute))
            {
                findings.Add(Skipped(attribute, type.Name, attribute.ApplicationSyntaxReference?.GetSyntax(cancellation).GetLocation()));
            }
        }

        if (type.AllInterfaces.Any(candidate => candidate.ToDisplayString() == Annotations + "IValidatableObject"))
        {
            findings.Add(new Finding(Descriptors.NotApplied.Id, $"IValidatableObject on '{type.Name}' can't be stated in the schema", location));
        }

        ReportConstructorAttributes(type, findings);
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (INamedTypeSymbol? current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (ISymbol member in current.GetMembers())
            {
                cancellation.ThrowIfCancellationRequested();

                // Whatever hides a name decides what value.Name means in the generated code.
                if (member is not (IPropertySymbol or IFieldSymbol) || member.IsImplicitlyDeclared || member.IsStatic || !seen.Add(member.Name))
                {
                    continue;
                }

                if (member is IPropertySymbol { IsIndexer: false } property && property.DeclaredAccessibility == Accessibility.Public)
                {
                    if (ReadProperty(property, type, contextName, findings) is { } model)
                    {
                        properties.Add(model);
                    }
                }
                else
                {
                    foreach (AttributeData attribute in member.GetAttributes())
                    {
                        if (IsRule(attribute))
                        {
                            findings.Add(Skipped(attribute, member.Name, member.Locations.FirstOrDefault()));
                        }
                    }
                }
            }
        }

        string typeCalls = Description(type.GetAttributes()) is { } text ? $".Description({Literal(text)})" : string.Empty;
        string? space = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
        bool hides = false;

        for (INamedTypeSymbol? parent = type.BaseType; parent is not null; parent = parent.BaseType)
        {
            hides |= parent.GetMembers("Schema").Any(member => member.DeclaredAccessibility != Accessibility.Private) || Has(parent.GetAttributes(), Attribute);

            // The generated property would hide a JSON property, and a rule on it couldn't be written.
            if (parent.GetMembers("Schema").OfType<IPropertySymbol>().Any(member => IsJsonProperty(member)))
            {
                Stop(Descriptors.NameTaken, type.Name);
            }
        }

        return new TypeModel(
            fullName,
            Hint(fullName),
            space,
            Declaration(type),
            Escape(type.Name),
            contextName,
            typeCalls,
            hides,
            new EquatableArray<PropertyModel>([.. properties]),
            new EquatableArray<Finding>([.. findings]),
            canGenerate);
    }

    private static string Hint(string fullName) =>
        new([.. fullName.Replace("global::", string.Empty).Select(character => char.IsLetterOrDigit(character) || character is '.' or '_' ? character : '_')]);

    private static string Escape(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    private static string Declaration(INamedTypeSymbol type) => (type.IsRecord, type.TypeKind) switch
    {
        (true, TypeKind.Struct) => "partial record struct",
        (true, _) => "partial record",
        (_, TypeKind.Struct) => "partial struct",
        _ => "partial class",
    };

    /// <summary>Why the JSON of a type isn't what its properties say, or null.</summary>
    private static string? OwnShape(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            ImmutableArray<AttributeData> attributes = current.GetAttributes();

            if (Has(attributes, Serialization + "JsonDerivedTypeAttribute") || Has(attributes, Serialization + "JsonPolymorphicAttribute"))
            {
                return "is polymorphic";
            }

            if (Has(attributes, Serialization + "JsonConverterAttribute"))
            {
                return "has a custom converter";
            }
        }

        return null;
    }

    private static string ReadContext(AttributeData attribute, INamedTypeSymbol type, Action<DiagnosticDescriptor, string> stop)
    {
        if (ContextOf(attribute) is not { } context)
        {
            stop(Descriptors.BadContext, "[GenerateSchema] needs a JsonSerializerContext");
            return string.Empty;
        }

        bool isContext = false;

        for (INamedTypeSymbol? current = context; current is not null; current = current.BaseType)
        {
            isContext |= current.ToDisplayString() == Serialization + "JsonSerializerContext";
        }

        if (!isContext)
        {
            stop(Descriptors.BadContext, $"'{context.Name}' isn't a JsonSerializerContext");
        }
        else if (!IsReachable(context, type))
        {
            stop(Descriptors.BadContext, $"'{context.Name}' has no [JsonSerializable(typeof({type.Name}))] that generates metadata");
        }

        return context.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    /// <summary>True when the context holds the type, listed or through the properties of what it lists.</summary>
    private static bool IsReachable(INamedTypeSymbol context, INamedTypeSymbol type)
    {
        HashSet<ITypeSymbol> seen = new(SymbolEqualityComparer.Default);
        Stack<ITypeSymbol> pending = new();

        AttributeData? options = context.GetAttributes().FirstOrDefault(attribute => NameOf(attribute) == Serialization + "JsonSourceGenerationOptionsAttribute");
        bool includesFields = options is not null && options.NamedArguments.Any(argument => argument.Key == "IncludeFields" && argument.Value.Value is true);

        foreach (AttributeData attribute in context.GetAttributes())
        {
            if (NameOf(attribute) == Serialization + "JsonSerializableAttribute" && attribute.ConstructorArguments is [{ Value: ITypeSymbol listed }, ..] && !IsFastPathOnly(attribute, options))
            {
                pending.Push(listed);
            }
        }

        while (pending.Count > 0)
        {
            ITypeSymbol current = pending.Pop();

            if (SymbolEqualityComparer.Default.Equals(current, type))
            {
                return true;
            }

            if (!seen.Add(current) || seen.Count > 10_000)
            {
                continue;
            }

            if (current is IArrayTypeSymbol array)
            {
                pending.Push(array.ElementType);
            }

            if (current is INamedTypeSymbol { IsGenericType: true } wrapper && wrapper.OriginalDefinition.ToDisplayString() is "System.Nullable<T>" or "Zorg.Json.Optional<T>" or "System.Memory<T>" or "System.ReadOnlyMemory<T>")
            {
                pending.Push(((INamedTypeSymbol)current).TypeArguments[0]);
            }

            // Only the items of a collection count. A type argument says nothing by itself: IComparable<T> or Func<T> gets no metadata.
            INamedTypeSymbol? items = SelfAndInterfaces(current).FirstOrDefault(candidate =>
                candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
                || candidate.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IAsyncEnumerable<T>");

            if (items is not null)
            {
                pending.Push(items.TypeArguments[0]);
                continue;
            }

            // What the context generates metadata for: what the serializer reads, also from a base type.
            foreach (ITypeSymbol owner in OwnersOf(current))
            {
                foreach (ISymbol member in owner.GetMembers())
                {
                    if (member is IPropertySymbol property && IsJsonProperty(property))
                    {
                        pending.Push(property.Type);
                    }
                    else if (member is IFieldSymbol field && IsJsonField(field, includesFields))
                    {
                        pending.Push(field.Type);
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Whether a listing only makes the fast path of the serializer, with no metadata for a schema to read.</summary>
    private static bool IsFastPathOnly(AttributeData listing, AttributeData? options)
    {
        const int SerializationOnly = 2;

        TypedConstant? mode = ModeOf(listing) ?? (options is null ? null : ModeOf(options));
        return mode?.Value is SerializationOnly;

        static TypedConstant? ModeOf(AttributeData attribute) =>
            attribute.NamedArguments.Where(argument => argument.Key == "GenerationMode").Select(argument => (TypedConstant?)argument.Value).FirstOrDefault();
    }

    private static IEnumerable<ITypeSymbol> OwnersOf(ITypeSymbol type)
    {
        for (ITypeSymbol? owner = type; owner is not null; owner = owner.BaseType)
        {
            yield return owner;
        }

        if (type.TypeKind == TypeKind.Interface)
        {
            foreach (INamedTypeSymbol parent in type.AllInterfaces)
            {
                yield return parent;
            }
        }
    }

    private static bool IsJsonProperty(IPropertySymbol property) =>
        property is { IsStatic: false, IsIndexer: false }
        && (property.DeclaredAccessibility == Accessibility.Public || IsIncluded(property.GetAttributes()))
        && !IsIgnored(property.GetAttributes());

    private static bool IsJsonField(IFieldSymbol field, bool includesFields) =>
        field is { IsStatic: false, IsImplicitlyDeclared: false }
        && ((includesFields && field.DeclaredAccessibility == Accessibility.Public) || IsIncluded(field.GetAttributes()))
        && !IsIgnored(field.GetAttributes());

    private static bool IsIncluded(ImmutableArray<AttributeData> attributes) => Has(attributes, Serialization + "JsonIncludeAttribute");

    private static INamedTypeSymbol? ContextOf(AttributeData attribute) =>
        attribute.ConstructorArguments is [{ Value: INamedTypeSymbol context }] ? context : null;

    /// <summary>The constructor System.Text.Json would call, or null when it can't be told.</summary>
    private static IMethodSymbol? ConstructorOf(INamedTypeSymbol type)
    {
        IMethodSymbol[] constructors = [.. type.InstanceConstructors.Where(constructor => !constructor.IsImplicitlyDeclared || constructor.Parameters.Length > 0)];
        IMethodSymbol[] chosen = [.. constructors.Where(constructor => Has(constructor.GetAttributes(), Serialization + "JsonConstructorAttribute"))];

        if (chosen.Length == 1)
        {
            return chosen[0];
        }

        // A struct always has a default constructor, and that's the one used unless another is marked.
        if (type.IsValueType)
        {
            return null;
        }

        // The copy constructor of a record is never the one.
        IMethodSymbol[] usable = [.. constructors.Where(constructor => constructor.DeclaredAccessibility == Accessibility.Public
            && !(constructor.Parameters is [{ Type: var only }] && SymbolEqualityComparer.Default.Equals(only, type)))];

        return usable.Length == 1 ? usable[0] : null;
    }

    private static void ReportConstructorAttributes(INamedTypeSymbol type, List<Finding> findings)
    {
        if (type.IsRecord)
        {
            return;
        }

        foreach (IParameterSymbol parameter in type.InstanceConstructors.SelectMany(constructor => constructor.Parameters))
        {
            foreach (AttributeData attribute in parameter.GetAttributes())
            {
                if (IsRule(attribute))
                {
                    findings.Add(Skipped(attribute, parameter.Name, parameter.Locations.FirstOrDefault()));
                }
            }
        }
    }

    /// <summary>The attributes of a property, what it overrides, and the parameter of a record, which is where they go without <c>property:</c>.</summary>
    private static ImmutableArray<AttributeData> AttributesOf(IPropertySymbol property)
    {
        ImmutableArray<AttributeData>.Builder all = ImmutableArray.CreateBuilder<AttributeData>();

        for (IPropertySymbol? current = property; current is not null; current = current.OverriddenProperty)
        {
            all.AddRange(current.GetAttributes());

            if (current.ContainingType.IsRecord)
            {
                foreach (IMethodSymbol constructor in current.ContainingType.InstanceConstructors)
                {
                    if (constructor.Parameters.FirstOrDefault(parameter => parameter.Name == current.Name) is { } match
                        && constructor.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() is RecordDeclarationSyntax))
                    {
                        all.AddRange(match.GetAttributes());
                    }
                }
            }
        }

        return all.ToImmutable();
    }

    private static bool IsIgnored(ImmutableArray<AttributeData> attributes) =>
        Has(attributes, Serialization + "JsonExtensionDataAttribute")
        || attributes.Any(attribute => NameOf(attribute) == Serialization + "JsonIgnoreAttribute"
            && !attribute.NamedArguments.Any(argument => argument.Key == "Condition" && argument.Value.Value is int and not 1));

    private static string? Description(ImmutableArray<AttributeData> attributes) =>
        attributes.FirstOrDefault(attribute => NameOf(attribute) == "System.ComponentModel.DescriptionAttribute")?.ConstructorArguments is [{ Value: string text }]
            ? text
            : null;

    private static string Literal(string text) => SymbolDisplay.FormatLiteral(text, quote: true);

    private static bool IsValidation(AttributeData attribute)
    {
        for (INamedTypeSymbol? current = attribute.AttributeClass; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == Annotations + "ValidationAttribute")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRule(AttributeData attribute) => IsValidation(attribute) || NameOf(attribute) == "System.ComponentModel.DescriptionAttribute";

    private static Finding Skipped(AttributeData attribute, string name, Location? location) =>
        new(Descriptors.NotApplied.Id, $"[{attribute.AttributeClass?.Name}] on '{name}' can't be stated in the schema", location);

    /// <summary>What a property's type allows the generated code to say.</summary>
    private sealed record Shape(string Kind, bool CanConfigure, ITypeSymbol? Number, ITypeSymbol? Nested, bool NestedIsItem, bool IsNullable);

    private static bool IsOptional(ITypeSymbol type) => type is INamedTypeSymbol { IsGenericType: true } named && named.OriginalDefinition.ToDisplayString() == "Zorg.Json.Optional<T>";

    private static bool IsNullableValue(ITypeSymbol type) => type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    private static bool IsNumber(ITypeSymbol type) => type.SpecialType is SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16
        or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
        or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal;

    private static IEnumerable<INamedTypeSymbol> SelfAndInterfaces(ITypeSymbol type) =>
        type is INamedTypeSymbol self ? type.AllInterfaces.Prepend(self) : type.AllInterfaces;

    /// <summary>The item type of a reference type written as a JSON array. Null for text, bytes and dictionaries.</summary>
    private static ITypeSymbol? ItemOf(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String || !type.IsReferenceType)
        {
            return null;
        }

        if (type is IArrayTypeSymbol array)
        {
            return array is { Rank: 1, ElementType.SpecialType: not SpecialType.System_Byte } ? array.ElementType : null;
        }

        if (SelfAndInterfaces(type).Any(candidate => candidate.OriginalDefinition.ToDisplayString() is "System.Collections.Generic.IDictionary<TKey, TValue>" or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>"))
        {
            return null;
        }

        return SelfAndInterfaces(type).FirstOrDefault(candidate => candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)?.TypeArguments[0];
    }

    /// <summary>A type with a generated schema that means the same here: the same context, so the same names.</summary>
    private static bool HasSameSchema(ITypeSymbol type, string context) => type.GetAttributes().Any(attribute => NameOf(attribute) == Attribute
        && ContextOf(attribute) is { } other && other.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == context);

    private static Shape ShapeOf(IPropertySymbol property, string context, ImmutableArray<AttributeData> attributes)
    {
        ITypeSymbol declared = property.Type;
        bool optional = IsOptional(declared);
        ITypeSymbol inner = optional ? ((INamedTypeSymbol)declared).TypeArguments[0] : declared;
        bool nullableValue = IsNullableValue(inner);
        ITypeSymbol core = nullableValue ? ((INamedTypeSymbol)inner).TypeArguments[0] : inner;
        bool nullable = nullableValue || core.NullableAnnotation == NullableAnnotation.Annotated;

        // A getter the generated code can read, and a shape the contract states.
        bool readable = property.GetMethod is { DeclaredAccessibility: Accessibility.Public };
        bool plain = readable && !Has(attributes, Serialization + "JsonConverterAttribute") && OwnShape(core as INamedTypeSymbol ?? property.ContainingType) is null;

        if (core.SpecialType == SpecialType.System_String)
        {
            return new Shape("string", plain, null, null, false, nullable);
        }

        if (core.SpecialType == SpecialType.System_Boolean)
        {
            return new Shape("boolean", plain && !nullableValue, null, null, false, nullable);
        }

        if (IsNumber(core))
        {
            return new Shape("number", plain, core, null, false, nullable);
        }

        if (ItemOf(core) is { } item)
        {
            bool nestedItem = plain && !optional && item.NullableAnnotation != NullableAnnotation.Annotated && !IsNullableValue(item) && HasSameSchema(item, context);
            return new Shape("array", plain && !optional, null, nestedItem ? item : null, nestedItem, nullable);
        }

        bool nested = plain && !nullableValue && !(optional && nullable) && HasSameSchema(core, context);
        return new Shape(nested ? "object" : "other", nested, null, nested ? core : null, false, nullable);
    }

    private static PropertyModel? ReadProperty(IPropertySymbol property, INamedTypeSymbol owner, string context, List<Finding> findings)
    {
        ImmutableArray<AttributeData> attributes = AttributesOf(property);

        if (IsIgnored(attributes))
        {
            return null;
        }

        Shape shape = ShapeOf(property, context, attributes);
        Rules rules = new();

        if (ItemOf(property.Type) is { NullableAnnotation: NullableAnnotation.Annotated } && !IsNullableValue(ItemOf(property.Type)!))
        {
            findings.Add(new Finding(Descriptors.NotApplied.Id, $"The items of '{property.Name}' may be null in C#, but a System.Text.Json contract can't say so, so a null item is rejected", property.Locations.FirstOrDefault()));
        }

        foreach (AttributeData attribute in attributes)
        {
            Location? location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? property.Locations.FirstOrDefault();

            if (!IsRule(attribute))
            {
                continue;
            }

            if (!shape.CanConfigure || !ReadAttribute(attribute, property, owner, shape, rules, findings, location))
            {
                findings.Add(Skipped(attribute, property.Name, location));
            }
        }

        string calls = rules.ToCalls(shape.Kind);

        if (calls.Length == 0 && shape.Nested is null)
        {
            return null;
        }

        return new PropertyModel(Escape(property.Name), calls, shape.Nested?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), shape.NestedIsItem);
    }

    /// <summary>Reads one attribute into the rules. False when it can't be stated here.</summary>
    private static bool ReadAttribute(AttributeData attribute, IPropertySymbol property, INamedTypeSymbol owner, Shape shape, Rules rules, List<Finding> findings, Location? location)
    {
        string? name = NameOf(attribute);
        int? first = attribute.ConstructorArguments is [{ Value: int value }, ..] ? value : null;
        object? Named(string key) => attribute.NamedArguments.FirstOrDefault(argument => argument.Key == key).Value.Value;
        bool isText = shape.Kind == "string";
        bool hasLength = isText || shape.Kind == "array";

        switch (name?.Replace(Annotations, string.Empty))
        {
            case "System.ComponentModel.DescriptionAttribute":
                rules.Description = attribute.ConstructorArguments is [{ Value: string text }] ? text : rules.Description;
                return true;

            case "MinLengthAttribute":
                return hasLength && first >= 0 && rules.AddLength(first, null);

            case "MaxLengthAttribute":
                // -1 is what the constructor without a length means: as much as the store allows.
                return hasLength && (first is null or -1 || (first >= 0 && rules.AddLength(null, first)));

            case "LengthAttribute":
                return hasLength && first >= 0 && attribute.ConstructorArguments is [_, { Value: int top }] && top >= first && rules.AddLength(first, top);

            case "StringLengthAttribute":
                return isText && first >= 0 && rules.AddLength(Named("MinimumLength") is int given and > 0 ? given : null, first);

            case "RegularExpressionAttribute":
                return isText && attribute.ConstructorArguments is [{ Value: string pattern }] && IsPortable(pattern) && rules.AddPattern($"^$|^(?:{pattern})$");

            case "EmailAddressAttribute":
                if (isText && rules.Format is null or ".Email()")
                {
                    rules.Format = ".Email()";
                    return true;
                }

                return false;

            case "UrlAttribute":
                // .NET takes only these three, and format: uri alone would let javascript: through.
                if (isText && rules.Format is null or ".Uri()")
                {
                    rules.Format = ".Uri()";
                    rules.AddPattern("^(?:[Hh][Tt][Tt][Pp][Ss]?|[Ff][Tt][Pp]):\\/\\/");
                    return true;
                }

                return false;

            case "RangeAttribute":
                return shape.Kind == "number" && ReadRange(attribute, shape.Number!, rules);

            case "RequiredAttribute":
                if (isText && Named("AllowEmptyStrings") is not true)
                {
                    rules.AddLength(1, null);
                    rules.NotBlank = true;
                }

                if (shape.IsNullable || !IsPresent(property, owner))
                {
                    findings.Add(new Finding(Descriptors.NotRequired.Id, property.Name, location));
                }

                return true;

            default:
                return false;
        }
    }

    /// <summary>True when the contract says the property must be there.</summary>
    private static bool IsPresent(IPropertySymbol property, INamedTypeSymbol owner) =>
        property.IsRequired
        || Has(property.GetAttributes(), Serialization + "JsonRequiredAttribute")
        || (ConstructorOf(owner)?.Parameters.Any(parameter => !parameter.HasExplicitDefaultValue && string.Equals(parameter.Name, property.Name, StringComparison.OrdinalIgnoreCase)) ?? false);

    /// <summary>
    /// True when the text reads as ECMA-262, which every Zorg pattern is. A pattern that doesn't fails when the schema is first built, so what
    /// can be told from here is told at compile time. Classes such as <c>\d</c> read, and mean less than in .NET, as the guide says.
    /// </summary>
    private static bool IsPortable(string pattern)
    {
        foreach (string dotNetOnly in new[] { "\\A", "\\z", "\\Z", "\\G", "(?i", "(?m", "(?s", "(?x", "(?n", "(?#", "(?>", "(?'", "(?-" })
        {
            if (pattern.IndexOf(dotNetOnly, StringComparison.Ordinal) >= 0)
            {
                return false;
            }
        }

        try
        {
            _ = new Regex(pattern, RegexOptions.ECMAScript, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool ReadRange(AttributeData attribute, ITypeSymbol type, Rules rules)
    {
        if (attribute.ConstructorArguments is not [{ Value: var low and (int or double) }, { Value: var high and (int or double) }])
        {
            return false;
        }

        bool Flag(string key) => attribute.NamedArguments.FirstOrDefault(argument => argument.Key == key).Value.Value is true;
        double minimum = Convert.ToDouble(low, CultureInfo.InvariantCulture);
        double maximum = Convert.ToDouble(high, CultureInfo.InvariantCulture);
        bool minimumIsExclusive = Flag("MinimumIsExclusive");
        bool maximumIsExclusive = Flag("MaximumIsExclusive");
        string cast = $"({type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})";

        if (double.IsNaN(minimum) || double.IsNaN(maximum))
        {
            return false;
        }

        if (IsWhole(type, out double smallest, out double largest))
        {
            // The bounds of a whole number are whole: 0.5 starts at 1, and an exclusive 1 stops at 0.
            double first = minimumIsExclusive ? Math.Floor(minimum) + 1 : Math.Ceiling(minimum);
            double last = maximumIsExclusive ? Math.Ceiling(maximum) - 1 : Math.Floor(maximum);

            // Nothing in the type can pass, which a bound of that type can't say.
            if (first > largest || last < smallest || first > last)
            {
                return false;
            }

            if (first > smallest)
            {
                rules.Minimum = $".Minimum({cast}{Whole(first)})";
            }

            if (last < largest)
            {
                rules.Maximum = $".Maximum({cast}{Whole(last)})";
            }

            return true;
        }

        (double lowest, double highest) = type.SpecialType switch
        {
            SpecialType.System_Single => (float.MinValue, float.MaxValue),
            SpecialType.System_Decimal => ((double)decimal.MinValue, (double)decimal.MaxValue),
            _ => (double.MinValue, double.MaxValue),
        };

        // Nothing in the type can pass, which a bound of that type can't say.
        if (minimum >= highest || maximum <= lowest)
        {
            return false;
        }

        if (minimum > lowest && !double.IsInfinity(minimum) && minimum < highest)
        {
            rules.Minimum = $".{(minimumIsExclusive ? "ExclusiveMinimum" : "Minimum")}({cast}{Real(minimum)})";
        }

        if (maximum < highest && !double.IsInfinity(maximum) && maximum > lowest)
        {
            rules.Maximum = $".{(maximumIsExclusive ? "ExclusiveMaximum" : "Maximum")}({cast}{Real(maximum)})";
        }

        return true;
    }

    private static bool IsWhole(ITypeSymbol type, out double smallest, out double largest)
    {
        (smallest, largest) = type.SpecialType switch
        {
            SpecialType.System_Byte => (byte.MinValue, byte.MaxValue),
            SpecialType.System_SByte => (sbyte.MinValue, sbyte.MaxValue),
            SpecialType.System_Int16 => (short.MinValue, short.MaxValue),
            SpecialType.System_UInt16 => (ushort.MinValue, ushort.MaxValue),
            SpecialType.System_Int32 => (int.MinValue, int.MaxValue),
            SpecialType.System_UInt32 => (uint.MinValue, uint.MaxValue),
            SpecialType.System_Int64 => (long.MinValue, long.MaxValue),
            SpecialType.System_UInt64 => (ulong.MinValue, ulong.MaxValue),
            _ => (0d, -1d),
        };

        return smallest <= largest;
    }

    private static string Whole(double value) => ((decimal)value).ToString("0", CultureInfo.InvariantCulture);

    private static string Real(double value) => value.ToString("R", CultureInfo.InvariantCulture) + "d";

    /// <summary>The rules of one property, kept until all attributes are read, because a later call of the same method replaces an earlier one.</summary>
    private sealed class Rules
    {
        private readonly List<string> _patterns = [];
        private int? _minimumLength;
        private int? _maximumLength;

        public string? Format { get; set; }

        /// <summary>Set by <c>[Required]</c>. Beside another pattern it's left out: a schema has one pattern, and combining them costs the linear engine.</summary>
        public bool NotBlank { get; set; }

        public string? Description { get; set; }

        public string? Minimum { get; set; }

        public string? Maximum { get; set; }

        public bool AddLength(int? minimum, int? maximum)
        {
            _minimumLength = minimum is { } low ? Math.Max(low, _minimumLength ?? low) : _minimumLength;
            _maximumLength = maximum is { } high ? Math.Min(high, _maximumLength ?? high) : _maximumLength;
            return true;
        }

        public bool AddPattern(string pattern)
        {
            _patterns.Add(pattern);
            return true;
        }

        public string ToCalls(string kind)
        {
            string calls = string.Empty;

            if (kind == "array")
            {
                calls += _minimumLength is { } low ? $".MinItems({low})" : string.Empty;
                calls += _maximumLength is { } high ? $".MaxItems({high})" : string.Empty;
            }
            else
            {
                calls += _minimumLength is { } low ? $".MinLength({low})" : string.Empty;
                calls += _maximumLength is { } high ? $".MaxLength({high})" : string.Empty;
            }

            if (NotBlank && _patterns.Count == 0)
            {
                _patterns.Add("\\S");
            }

            // Several patterns all apply, and a schema has one.
            if (_patterns.Count == 1)
            {
                calls += $".Pattern({Literal(_patterns[0])})";
            }
            else if (_patterns.Count > 1)
            {
                calls += $".Pattern({Literal(string.Concat(_patterns.Select(pattern => $"(?=[\\s\\S]*?(?:{pattern}))")))})";
            }

            return calls + Format + Minimum + Maximum + (Description is { } text ? $".Description({Literal(text)})" : string.Empty);
        }
    }
}
