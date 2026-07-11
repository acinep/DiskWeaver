namespace DiskWeaver.Executor;

/// <summary>
/// One command in an <see cref="ExecutionPlan"/>. A step with a null
/// <see cref="Command"/> is advisory only (rendered as a comment, never
/// invoked) — used for things like noting reserved/unallocated space.
/// </summary>
/// <param name="Description">Human-readable summary shown before the command.</param>
/// <param name="Command">The program to invoke, or null for a comment-only step.</param>
/// <param name="Arguments">Arguments passed to <see cref="Command"/>.</param>
public sealed record ExecutionStep(string Description, string? Command, IReadOnlyList<string> Arguments)
{
    public static ExecutionStep Comment(string description) => new(description, null, []);
}
