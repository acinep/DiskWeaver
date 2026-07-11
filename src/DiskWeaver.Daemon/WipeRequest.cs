namespace DiskWeaver.Daemon;

/// <summary>Disks to clear via <c>POST /disks/wipe</c> -- see <see cref="DiskWeaver.Executor.CommandPlanner.BuildWipe"/>.</summary>
public sealed record WipeRequest(string[] DiskIds);
