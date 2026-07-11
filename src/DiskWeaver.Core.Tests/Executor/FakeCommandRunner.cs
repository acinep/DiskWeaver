using DiskWeaver.Core.Executor.Abstractions;

namespace DiskWeaver.Executor.Tests;

/// <summary>
/// Canned command responses keyed by the exact command+arguments invoked -- throws if anything
/// unexpected is run, so a test using this catches not just wrong output but wrong *commands*
/// (e.g. querying the wrong device path).
/// </summary>
public sealed class FakeCommandRunner : ICommandRunner
{
    private readonly Dictionary<string, string> _responses = [];

    public void Respond(string command, IReadOnlyList<string> arguments, string output) =>
        _responses[Key(command, arguments)] = output;

    public string Run(string command, IReadOnlyList<string> arguments)
    {
        var key = Key(command, arguments);
        return _responses.TryGetValue(key, out var output)
            ? output
            : throw new InvalidOperationException($"No canned response for: {command} {string.Join(' ', arguments)}");
    }

    private static string Key(string command, IReadOnlyList<string> arguments) =>
        $"{command} {string.Join(' ', arguments)}";
}
