using System;

namespace Zorg.Json.Model;

/// <summary>The JSON Schema <c>type</c> keyword, as flags because a schema may allow several types.</summary>
[Flags]
public enum JsonTypes
{
    /// <summary>No <c>type</c> keyword: every type is allowed.</summary>
    None = 0,

    /// <summary>The JSON Schema type <c>null</c>.</summary>
    Null = 1,

    /// <summary>The JSON Schema type <c>boolean</c>.</summary>
    Boolean = 2,

    /// <summary>The JSON Schema type <c>integer</c>.</summary>
    Integer = 4,

    /// <summary>The JSON Schema type <c>number</c>.</summary>
    Number = 8,

    /// <summary>The JSON Schema type <c>string</c>.</summary>
    String = 16,

    /// <summary>The JSON Schema type <c>array</c>.</summary>
    Array = 32,

    /// <summary>The JSON Schema type <c>object</c>.</summary>
    Object = 64,
}
