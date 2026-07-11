using DiskWeaver.Core.Executor.Abstractions;
using System.Diagnostics;

namespace DiskWeaver.Executor;

/// <summary>Runs a command as a real subprocess and returns its stdout. Linux-only in practice (pvs/lvs/mdadm/blockdev).</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    public string Run(string command, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start {command}.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Could not run {command}. This source only works on a Linux host with mdadm/lvm2/util-linux installed.", ex);
        }

        // See ProcessStepRunner.Run for why these must be drained concurrently rather than one
        // ReadToEnd() after another: whichever stream isn't being read yet can fill its pipe
        // buffer and block the child forever, and a sequential read never gets to it in time.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        Task.WaitAll(outputTask, errorTask);
        process.WaitForExit();
        var output = outputTask.Result;
        var error = errorTask.Result;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{command} exited with code {process.ExitCode}: {error}");
        }

        return output;
    }
}
