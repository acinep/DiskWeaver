using DiskWeaver.Core.Executor.Abstractions;
using System.Diagnostics;

namespace DiskWeaver.Executor;

/// <summary>
/// Runs an <see cref="ExecutionStep"/>'s command as a real subprocess. Linux-only in practice (mdadm/parted/lvm2).
/// </summary>
/// <remarks>
/// No timeout or cancellation path, deliberately not added: <c>mdadm --wait</c> is a real step
/// this runs (see <see cref="CommandPlanner.BuildGrowSteps"/> via <see cref="CommandPlanner.BuildIncremental"/>)
/// and can legitimately block for hours on real hardware reshaping a large array, so any timeout
/// short enough to matter would abort a healthy, still-in-progress operation, not a stuck one --
/// there's no duration that's simultaneously "clearly hung" and "definitely not just a big
/// reshape." A correct fix needs the daemon to run steps like this one in the background with a
/// polling/cancel API (<c>POST /execute/{id}/abort</c>, already noted as missing in
/// docs/execution.md's "Open items"), not a fixed timeout here. Since the daemon's execute
/// endpoints currently run synchronously within one HTTP request regardless, this runner matches
/// that model rather than half-implementing cancellation the caller can't yet use.
/// </remarks>
public sealed class ProcessStepRunner : IStepRunner
{
    public ExecutionStepOutcome Run(ExecutionStep step)
    {
        if (step.Command is null)
        {
            throw new InvalidOperationException("Comment-only steps have no command to run.");
        }

        var startInfo = new ProcessStartInfo(step.Command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in step.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {step.Command}.");

        // Reading stdout to completion before even starting on stderr (or vice versa) deadlocks
        // once a child writes enough to fill the OS pipe buffer on the stream nobody's draining
        // yet -- it blocks on that write forever, and this call never gets past ReadToEnd() to
        // read the other stream and unblock it. Draining both concurrently avoids that regardless
        // of which stream (or how much of it) the child fills first.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        Task.WaitAll(outputTask, errorTask);
        process.WaitForExit();

        return new ExecutionStepOutcome(process.ExitCode, outputTask.Result, errorTask.Result);
    }
}
