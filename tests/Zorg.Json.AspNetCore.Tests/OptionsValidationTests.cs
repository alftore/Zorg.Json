using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Zorg.Json.AspNetCore.Tests;

public sealed class OptionsValidationTests
{
    private static readonly ObjectSchema SmtpSchema = Z.Object()
        .Property("host", Z.String().MinLength(1))
        .Property("port", Z.Integer().Minimum(1).Maximum(65535));

    private static ServiceProvider Provider(Action<SmtpOptions> configure, string? name = null)
    {
        ServiceCollection services = new();
        services.AddOptions<SmtpOptions>(name).Configure(configure).ValidateWithSchema(SmtpSchema, TestJson.Default.SmtpOptions);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Valid_options_pass()
    {
        using ServiceProvider provider = Provider(options =>
        {
            options.Host = "mail";
            options.Port = 25;
        });

        Assert.Equal(25, provider.GetRequiredService<IOptions<SmtpOptions>>().Value.Port);
    }

    [Fact]
    public void Failures_name_the_json_pointer()
    {
        using ServiceProvider provider = Provider(options => options.Host = "mail");

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<SmtpOptions>>().Value);

        Assert.StartsWith("SmtpOptions/port: ", Assert.Single(exception.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Other_named_options_are_skipped()
    {
        using ServiceProvider provider = Provider(_ => { }, "primary");

        IOptionsMonitor<SmtpOptions> monitor = provider.GetRequiredService<IOptionsMonitor<SmtpOptions>>();

        Assert.Equal(0, monitor.Get("secondary").Port);
        Assert.Throws<OptionsValidationException>(() => monitor.Get("primary"));
    }

    [Fact]
    public async Task ValidateOnStart_stops_the_host()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddOptions<SmtpOptions>()
            .ValidateWithSchema(SmtpSchema, TestJson.Default.SmtpOptions)
            .ValidateOnStart();

        using IHost host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }
}
