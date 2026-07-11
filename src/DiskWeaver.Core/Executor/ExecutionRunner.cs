using DiskWeaver.Core.Executor.Abstractions;

namespace DiskWeaver.Executor;

/// <summary>
/// Advances an <see cref="ExecutionPlan"/>'s execution by exactly one step at a time. Pure
/// aside from delegating the actual command invocation to <see cref="IStepRunner"/> -- fully
/// unit-testable with a fake runner, no real subprocesses needed.
/// </summary>
/// <remarks>
/// Callers persist the returned journal (via <see cref="IJournalStore"/>) before calling
/// <see cref="AdvanceOneStep"/> again, so a crash mid-run only ever loses progress on the one
/// step that was in flight, never anything already recorded as succeeded. Resuming after a
/// crash, or retrying after a failed step, is the same operation as running for the first time:
/// call this again with whatever journal (if any) was last persisted.
/// </remarks>
public static class ExecutionRunner
{
    public static ExecutionJournal AdvanceOneStep(
        string executionId, string kind, ExecutionPlan plan, ExecutionJournal? journal, IStepRunner runner)
    {
        var records = journal?.Steps.ToList() ?? [];
        var nextIndex = records.Count(r => r.Status == ExecutionStepStatus.Succeeded);

        if (nextIndex >= plan.Steps.Count)
        {
            return journal ?? new ExecutionJournal(executionId, kind, ExecutionJournalStatus.Succeeded, records);
        }

        var step = plan.Steps[nextIndex];
        var record = step.Command is null
            ? new ExecutionStepRecord(nextIndex, step.Description, null, step.Arguments, ExecutionStepStatus.Succeeded, null, null, null)
            : RunCommandStep(nextIndex, step, runner);

        records = records.Take(nextIndex).ToList();
        records.Add(record);

        var status = record.Status == ExecutionStepStatus.Failed
            ? ExecutionJournalStatus.Failed
            : records.Count == plan.Steps.Count
                ? ExecutionJournalStatus.Succeeded
                : ExecutionJournalStatus.Running;

        return new ExecutionJournal(executionId, kind, status, records);
    }

    private static ExecutionStepRecord RunCommandStep(int index, ExecutionStep step, IStepRunner runner)
    {
        var outcome = runner.Run(step);
        var succeeded = outcome.ExitCode == 0;
        return new ExecutionStepRecord(
            index,
            step.Description,
            step.Command,
            step.Arguments,
            succeeded ? ExecutionStepStatus.Succeeded : ExecutionStepStatus.Failed,
            outcome.ExitCode,
            outcome.Output,
            outcome.Error);
    }
}
