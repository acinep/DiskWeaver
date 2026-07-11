using DiskWeaver.Executor.Tests;

namespace DiskWeaver.Daemon.Tests;

public class DiskWeaverAccessPolicyTests
{
    [Fact]
    public void Uid0IsAlwaysAuthorized_EvenWithoutTheGroup()
    {
        var runner = new FakeCommandRunner();
        runner.Respond("id", ["-u", "root"], "0\n");

        var policy = new DiskWeaverAccessPolicy(runner);

        Assert.True(policy.IsAuthorized("root"));
    }

    [Fact]
    public void MemberOfDiskweaverGroupIsAuthorized()
    {
        var runner = new FakeCommandRunner();
        runner.Respond("id", ["-u", "noel"], "1000\n");
        runner.Respond("id", ["-Gn", "noel"], "noel sudo diskweaver\n");

        var policy = new DiskWeaverAccessPolicy(runner);

        Assert.True(policy.IsAuthorized("noel"));
    }

    [Fact]
    public void UserOutsideBothIsRejected()
    {
        var runner = new FakeCommandRunner();
        runner.Respond("id", ["-u", "guest"], "1001\n");
        runner.Respond("id", ["-Gn", "guest"], "guest\n");

        var policy = new DiskWeaverAccessPolicy(runner);

        Assert.False(policy.IsAuthorized("guest"));
    }
}
