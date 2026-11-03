using System.Reflection;
using Xunit;

namespace Zorg.Json.Tests;

/// <summary>Proves the test tooling works. Delete once real tests exist.</summary>
public sealed class ScaffoldTests
{
    [Fact]
    public void Core_assembly_loads() => Assert.Equal("Zorg.Json", Assembly.Load("Zorg.Json").GetName().Name);
}
