using DiskWeaver.Executor;

namespace DiskWeaver.Core.Executor.Abstractions;

/// <summary>Runs a read-only command and returns its stdout. Exists so state-collecting code (e.g. <see cref="MdadmLvmPoolStateSource"/>) can be tested without real subprocesses.</summary>
public interface ICommandRunner
{
    string Run(string command, IReadOnlyList<string> arguments);
}
