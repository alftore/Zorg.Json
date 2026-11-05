using Xunit;

namespace Zorg.Json.Tests;

/// <summary>Tests that measure allocation count the whole process, so they run with nothing else beside them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationSensitive
{
    public const string Name = "Allocation sensitive";
}
