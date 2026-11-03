using System;
using System.Collections.Immutable;

namespace Zorg.Json;

/// <summary>
/// The outcome of <see cref="Schema{T}.SafeParse(string)"/>: either a typed value or the issues
/// explaining why there isn't one. Zod equivalent: the object returned by <c>safeParse</c>.
/// </summary>
/// <typeparam name="T">The type of the parsed value.</typeparam>
/// <example>
/// <code>
/// ParseResult&lt;string&gt; result = Z.String().MinLength(1).SafeParse(json);
/// if (result.IsSuccess)
/// {
///     Console.WriteLine(result.Value);
/// }
/// else
/// {
///     Console.WriteLine(result.Issues.ToPrettyString());
/// }
/// </code>
/// </example>
public readonly record struct ParseResult<T>
{
    private readonly T _value;
    private readonly ImmutableArray<ParseIssue> _issues;

    private ParseResult(T value, bool isSuccess, ImmutableArray<ParseIssue> issues)
    {
        _value = value;
        IsSuccess = isSuccess;
        _issues = issues;
    }

    /// <summary>Whether the input was valid. False for <c>default(ParseResult&lt;T&gt;)</c>.</summary>
    public bool IsSuccess { get; }

    /// <summary>The parsed value.</summary>
    /// <exception cref="InvalidOperationException">The parse failed. Check <see cref="IsSuccess"/> first.</exception>
    public T Value => IsSuccess ? _value : throw new InvalidOperationException("The parse failed, so there is no value. Check IsSuccess first.");

    /// <summary>The reasons for failure. Empty on success.</summary>
    public ImmutableArray<ParseIssue> Issues => _issues.IsDefault ? [] : _issues;

    internal static ParseResult<T> Success(T value) => new(value, isSuccess: true, []);

    internal static ParseResult<T> Failure(ImmutableArray<ParseIssue> issues) => new(default!, isSuccess: false, issues);

    /// <summary>Converts a successful value and passes failures through. Use it to map a DTO to a domain object.</summary>
    /// <typeparam name="TResult">The type to convert to.</typeparam>
    /// <param name="map">The conversion, which only runs on success.</param>
    /// <returns>The converted result.</returns>
    /// <example>
    /// <code>
    /// ParseResult&lt;Uri&gt; result = Z.String().Uri().SafeParse(json).Map(text => new Uri(text));
    /// </code>
    /// </example>
    public ParseResult<TResult> Map<TResult>(Func<T, TResult> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? ParseResult<TResult>.Success(map(_value)) : ParseResult<TResult>.Failure(Issues);
    }

    /// <summary>Continues with a step that can fail too, and passes failures through.</summary>
    /// <typeparam name="TResult">The value type of the next step.</typeparam>
    /// <param name="bind">The next step, which only runs on success.</param>
    /// <returns>The result of the next step.</returns>
    /// <example>
    /// <code>
    /// ParseResult&lt;string&gt; result = first.SafeParse(json).Bind(text => second.SafeParse(text));
    /// </code>
    /// </example>
    public ParseResult<TResult> Bind<TResult>(Func<T, ParseResult<TResult>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? bind(_value) : ParseResult<TResult>.Failure(Issues);
    }

    /// <inheritdoc/>
    public override string ToString() => IsSuccess ? $"Success: {_value}" : $"Failure: {Issues.Length} issue(s)";
}
