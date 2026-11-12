using System;
using System.Diagnostics;
using Xunit;

namespace Zorg.Json.ComplianceTests;

/// <summary>
/// A wall-clock budget for work that would blow up if an algorithm went quadratic. CI runs the test assemblies of both target
/// frameworks at once, so a busy machine can pause any one run: work over budget runs again, and fails only if it's slow twice.
/// Work that really blows up is slow every time.
/// </summary>
internal static class Timing
{
    public static T Within<T>(TimeSpan budget, Func<T> work)
    {
        Stopwatch clock = Stopwatch.StartNew();
        T result = work();

        if (clock.Elapsed < budget)
        {
            return result;
        }

        TimeSpan first = clock.Elapsed;
        clock.Restart();
        result = work();

        Assert.True(clock.Elapsed < budget, $"took {first}, then {clock.Elapsed}, against a budget of {budget}");
        return result;
    }

    public static void Within(TimeSpan budget, Action work) =>
        Within(budget, () =>
        {
            work();
            return 0;
        });
}
