using DiskWeaver.Executor;

namespace DiskWeaver.Core.Executor.Abstractions;

/// <summary>
/// A source of already-built pool state (from real mdadm/LVM, or a fake for testing). DiskWeaver
/// never persists its own copy of "what pools exist" -- see docs/state-model.md -- so this is the
/// only way the planner/daemon learns about pools that already exist on disk.
/// </summary>
public interface IPoolStateSource
{
    IReadOnlyList<ExistingPoolState> GetPools();
}
