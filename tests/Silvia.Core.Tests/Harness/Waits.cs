using Silvia.Core.Projects;

namespace Silvia.Core.Tests.Harness;

/// <summary>Waiting for asynchronous work in tests, with a deadline so a hang fails instead of stalling.</summary>
public static class Waits
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds:F0} s waiting for: {because}");
            }

            await Task.Delay(50);
        }
    }
}

/// <summary>Thrown by <see cref="CrashAt"/> to stop a project operation as a process crash would.</summary>
public sealed class SimulatedCrash(string step) : Exception($"Simulated crash at '{step}'.");

/// <summary>Crashes the project operation when it reaches the named step.</summary>
public sealed class CrashAt(string step) : IProjectFaults
{
    public void Step(string name)
    {
        if (name == step)
        {
            throw new SimulatedCrash(name);
        }
    }
}
