using DiskWeaver.Core.Executor.Abstractions;
using DiskWeaver.Executor;

namespace DiskWeaver.Daemon.Tests;

/// <summary>Records every step it's asked to run and always reports success -- no real mdadm/parted/lvm2 involved.</summary>
public sealed class FakeStepRunner : IStepRunner
{
    public List<ExecutionStep> Invocations { get; } = [];

    public ExecutionStepOutcome Run(ExecutionStep step)
    {
        Invocations.Add(step);
        return new ExecutionStepOutcome(0, "ok", "");
    }
}
