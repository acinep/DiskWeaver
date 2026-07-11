using DiskWeaver.Core.Executor.Abstractions;

namespace DiskWeaver.Executor.Tests;

public class ExecutionRunnerTests
{
    private sealed class ScriptedStepRunner : IStepRunner
    {
        private readonly Queue<ExecutionStepOutcome> _outcomes;

        public ScriptedStepRunner(params ExecutionStepOutcome[] outcomes) => _outcomes = new Queue<ExecutionStepOutcome>(outcomes);

        public List<ExecutionStep> Invocations { get; } = [];

        public ExecutionStepOutcome Run(ExecutionStep step)
        {
            Invocations.Add(step);
            return _outcomes.Count > 0 ? _outcomes.Dequeue() : new ExecutionStepOutcome(0, "", "");
        }
    }

    private static ExecutionPlan ThreeStepPlan() => new(
    [
        new ExecutionStep("step 0", "cmd0", ["a"]),
        new ExecutionStep("step 1", "cmd1", ["b"]),
        new ExecutionStep("step 2", "cmd2", ["c"]),
    ]);

    [Fact]
    public void AdvancesOneStepAtATime_RunningUntilAllSucceed()
    {
        var runner = new ScriptedStepRunner();
        var plan = ThreeStepPlan();

        var journal = ExecutionRunner.AdvanceOneStep("exec-1", "build", plan, null, runner);
        Assert.Equal(ExecutionJournalStatus.Running, journal.Status);
        Assert.Single(journal.Steps);
        Assert.Equal(ExecutionStepStatus.Succeeded, journal.Steps[0].Status);

        journal = ExecutionRunner.AdvanceOneStep("exec-1", "build", plan, journal, runner);
        Assert.Equal(ExecutionJournalStatus.Running, journal.Status);
        Assert.Equal(2, journal.Steps.Count);

        journal = ExecutionRunner.AdvanceOneStep("exec-1", "build", plan, journal, runner);
        Assert.Equal(ExecutionJournalStatus.Succeeded, journal.Status);
        Assert.Equal(3, journal.Steps.Count);
        Assert.Equal(3, runner.Invocations.Count);
    }

    [Fact]
    public void StepFailure_StopsAdvancing_AndCanBeRetried()
    {
        var runner = new ScriptedStepRunner(
            new ExecutionStepOutcome(0, "ok", ""),
            new ExecutionStepOutcome(1, "", "boom"));
        var plan = ThreeStepPlan();

        var journal = ExecutionRunner.AdvanceOneStep("exec-2", "build", plan, null, runner);
        journal = ExecutionRunner.AdvanceOneStep("exec-2", "build", plan, journal, runner);

        Assert.Equal(ExecutionJournalStatus.Failed, journal.Status);
        Assert.Equal(2, journal.Steps.Count);
        Assert.Equal(ExecutionStepStatus.Failed, journal.Steps[1].Status);
        Assert.Equal(1, journal.Steps[1].ExitCode);
        Assert.Equal("boom", journal.Steps[1].Error);

        // Retrying re-runs exactly the failed step, not the ones already succeeded.
        journal = ExecutionRunner.AdvanceOneStep("exec-2", "build", plan, journal, runner);
        Assert.Equal(ExecutionJournalStatus.Running, journal.Status);
        Assert.Equal(ExecutionStepStatus.Succeeded, journal.Steps[1].Status);
        Assert.Equal(3, runner.Invocations.Count);
        Assert.Equal("cmd1", runner.Invocations[2].Command);
    }

    [Fact]
    public void CommentOnlySteps_SucceedWithoutInvokingTheRunner()
    {
        var runner = new ScriptedStepRunner();
        var plan = new ExecutionPlan([ExecutionStep.Comment("just a note"), new ExecutionStep("real step", "cmd", [])]);

        var journal = ExecutionRunner.AdvanceOneStep("exec-3", "build", plan, null, runner);

        Assert.Equal(ExecutionStepStatus.Succeeded, journal.Steps[0].Status);
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public void AlreadyCompletePlan_IsIdempotent()
    {
        var runner = new ScriptedStepRunner();
        var plan = ThreeStepPlan();

        var journal = ExecutionRunner.AdvanceOneStep("exec-4", "build", plan, null, runner);
        journal = ExecutionRunner.AdvanceOneStep("exec-4", "build", plan, journal, runner);
        journal = ExecutionRunner.AdvanceOneStep("exec-4", "build", plan, journal, runner);
        Assert.Equal(ExecutionJournalStatus.Succeeded, journal.Status);

        var again = ExecutionRunner.AdvanceOneStep("exec-4", "build", plan, journal, runner);

        Assert.Equal(journal, again);
        Assert.Equal(3, runner.Invocations.Count);
    }
}
