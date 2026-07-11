namespace DiskWeaver.Executor.Tests;

public class LoopDeviceScriptEmitterTests
{
    [Fact]
    public void RendersOneTruncateAndLosetupPerDisk()
    {
        var script = LoopDeviceScriptEmitter.Render([2_000_000_000L, 2_000_000_000L, 4_000_000_000L]);

        Assert.Contains("truncate -s 2000000000 disk0.img", script);
        Assert.Contains("truncate -s 2000000000 disk1.img", script);
        Assert.Contains("truncate -s 4000000000 disk2.img", script);
        Assert.Contains("DEV[0]=$(sudo losetup -fP --show disk0.img)", script);
        Assert.Contains("DEV[2]=$(sudo losetup -fP --show disk2.img)", script);
    }

    [Fact]
    public void UsesProvidedWorkDir()
    {
        var script = LoopDeviceScriptEmitter.Render([1_000_000_000L], workDir: "/tmp/rig");

        Assert.Contains("mkdir -p /tmp/rig", script);
        Assert.Contains("cd /tmp/rig", script);
    }

    [Fact]
    public void CapturesInventoryScopedToOnlyTheLoopDevicesItCreated()
    {
        // Critical safety property: a plain system-wide `lsblk` capture would also pick up
        // real production disks. The script must scope the capture to just its own $DEV array.
        var script = LoopDeviceScriptEmitter.Render([2_000_000_000L, 2_000_000_000L]);

        Assert.Contains("""lsblk --json -b -o NAME,SIZE,TYPE,ID-LINK,FSTYPE,MOUNTPOINTS "${DEV[@]}" > inventory.json""", script);
    }
}
