using DiskWeaver.Core.Executor.Abstractions;
using DiskWeaver.Executor;

namespace DiskWeaver.Daemon;

// PAM (PamAuthenticator) only proves *who* someone is -- it says nothing about whether they
// should be allowed to drive a root daemon that can repartition real disks. This is the same
// authorization boundary the Unix socket already enforces (packaging/diskweaverd.service's
// Group=diskweaver/UMask=0007: only root or a member of the "diskweaver" group can even open the
// socket), applied here for the standalone SPA's login instead. Uses `id` rather than hand-rolled
// getpwnam/getgrnam P/Invoke -- same "shell out to a small trusted system tool, parse its output"
// idiom as MdadmDetailParser/ProcMdstatParser, and glibc's non-reentrant getpwnam/getgrnam would
// need real care to call safely from Kestrel's multi-threaded request handling anyway.
public sealed class DiskWeaverAccessPolicy(ICommandRunner? commandRunner = null)
{
    private const string RequiredGroup = "diskweaver";

    private readonly ICommandRunner _runner = commandRunner ?? new ProcessCommandRunner();

    public bool IsAuthorized(string username)
    {
        var uid = _runner.Run("id", ["-u", username]).Trim();
        if (uid == "0")
        {
            return true;
        }

        var groups = _runner.Run("id", ["-Gn", username])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return groups.Contains(RequiredGroup, StringComparer.Ordinal);
    }
}
