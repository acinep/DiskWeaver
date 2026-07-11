using DiskWeaver.Executor;

namespace DiskWeaver.Core.Executor.Abstractions;

/// <summary>Actually runs one <see cref="ExecutionStep"/>'s command. Exists so <see cref="ExecutionRunner"/> can be tested without real subprocesses.</summary>
public interface IStepRunner
{
    /// <summary>Runs <paramref name="step"/>. Never called for a comment-only step (null <c>Command</c>).</summary>
    ExecutionStepOutcome Run(ExecutionStep step);
}
