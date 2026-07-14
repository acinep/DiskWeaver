using DiskWeaver.Executor;

namespace DiskWeaver.Cli;

/// <summary>
/// Implements `diskweaver reassemble [--script <file>]` -- emits the mdadm --assemble --scan /
/// vgchange -ay script that brings up any array/VG that already exists on disk (e.g. after
/// installing a fresh OS onto disks that already hold a DiskWeaver pool) but isn't currently
/// assembled/active, so `diskweaver inventory`/the daemon's GET /pools can see it. Only ever emits
/// a script for the user to review and run, same as `wipe`/`plan --script` -- this CLI never
/// executes commands itself. (The daemon's POST /arrays/reassemble endpoint does execute this
/// directly, for the Cockpit UI's equivalent button -- see docs/cockpit-plugin.md.)
/// </summary>
public static class ReassembleCommand
{
    public static int Run(string[] args)
    {
        string? scriptOutputPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--script" && i + 1 < args.Length)
            {
                scriptOutputPath = args[++i];
            }
        }

        var plan = CommandPlanner.BuildReassemble();
        var script = ShellScriptEmitter.Render(plan);

        if (scriptOutputPath is not null)
        {
            File.WriteAllText(scriptOutputPath, script);
            Console.WriteLine($"Reassemble script written to {scriptOutputPath}");
        }
        else
        {
            Console.WriteLine(script);
        }

        return 0;
    }
}
