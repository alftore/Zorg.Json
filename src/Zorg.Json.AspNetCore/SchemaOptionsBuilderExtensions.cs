using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Zorg.Json.AspNetCore;

/// <summary>Validates options against a schema.</summary>
public static class SchemaOptionsBuilderExtensions
{
    /// <summary>
    /// Validates options by serializing them with <paramref name="typeInfo"/> and parsing the JSON with
    /// <paramref name="schema"/>. Add <c>ValidateOnStart</c> to fail at startup instead of at first use.
    /// </summary>
    /// <typeparam name="TOptions">The options type.</typeparam>
    /// <typeparam name="TParsed">The type the schema parses to, which doesn't have to be <typeparamref name="TOptions"/>.</typeparam>
    /// <param name="builder">The options builder.</param>
    /// <param name="schema">The schema. It can't have async refinements, because options validation is synchronous.</param>
    /// <param name="typeInfo">The JSON contract of the options, which also decides the property names the schema sees.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddOptions&lt;SmtpOptions&gt;()
    ///     .BindConfiguration("Smtp")
    ///     .ValidateWithSchema(SmtpSchema, AppJson.Default.SmtpOptions)
    ///     .ValidateOnStart();
    /// </code>
    /// </example>
    public static OptionsBuilder<TOptions> ValidateWithSchema<TOptions, TParsed>(
        this OptionsBuilder<TOptions> builder,
        Schema<TParsed> schema,
        JsonTypeInfo<TOptions> typeInfo)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(typeInfo);

        builder.Services.AddSingleton<IValidateOptions<TOptions>>(new SchemaValidateOptions<TOptions, TParsed>(builder.Name, schema, typeInfo));
        return builder;
    }

    private sealed class SchemaValidateOptions<TOptions, TParsed>(string? optionsName, Schema<TParsed> schema, JsonTypeInfo<TOptions> typeInfo)
        : IValidateOptions<TOptions>
        where TOptions : class
    {
        public ValidateOptionsResult Validate(string? name, TOptions options)
        {
            if (optionsName is not null && optionsName != name)
            {
                return ValidateOptionsResult.Skip;
            }

            ParseResult<TParsed> result = schema.SafeParse(JsonSerializer.SerializeToElement(options, typeInfo));

            return result.IsSuccess
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(result.Issues.Select(issue =>
                    $"{typeof(TOptions).Name}{issue.InstanceLocation}: {issue.Message}"));
        }
    }
}
