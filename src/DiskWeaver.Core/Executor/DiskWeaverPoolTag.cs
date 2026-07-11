namespace DiskWeaver.Executor;

/// <summary>
/// The LVM tag <see cref="CommandPlanner"/> applies to every volume group it creates (via
/// <c>vgcreate --addtag</c>), and the only signal <see cref="MdadmLvmPoolStateSource"/> trusts to
/// mean "this pool belongs to DiskWeaver" -- naming (<c>diskweaver-pool</c>) is human-readable but
/// not authoritative, since nothing stops an unrelated VG from being named the same thing, or a
/// DiskWeaver-created VG from being renamed. See docs/state-model.md's ownership section.
/// </summary>
public static class DiskWeaverPoolTag
{
    public const string Value = "diskweaver-managed";
}
