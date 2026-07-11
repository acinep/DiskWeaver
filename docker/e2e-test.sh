#!/usr/bin/env bash
set -euo pipefail

# Runs the full loop-device validation workflow from docs/testing.md end-to-end,
# non-interactively: testkit -> plan -> build -> mkfs/mount -> verify -> teardown.
# Requires the container to run with --privileged (loop devices, mdadm, lvm all
# need real kernel access -- see docker/README.md).

CLI="dotnet run --no-build -c Release --project src/DiskWeaver.Cli --"
WORKDIR="/tmp/diskweaver-e2e"
MOUNTPOINT="/mnt/diskweaver-e2e"

rm -rf "$WORKDIR"
umount "$MOUNTPOINT" 2>/dev/null || true
mkdir -p "$WORKDIR" "$MOUNTPOINT"

echo "== 1/6: Generating loop-device setup script =="
$CLI testkit --disks 2GB,2GB,4GB,4GB,4GB --dir "$WORKDIR" --out "$WORKDIR/setup-loop-devices.sh"

echo "== 2/6: Creating loop devices + capturing scoped inventory =="
bash "$WORKDIR/setup-loop-devices.sh"

echo "== 3/6: Planning pool (DWR-1) =="
disk_names=$(jq -r '[.blockdevices[].name] | join(",")' "$WORKDIR/inventory.json")
$CLI plan --lsblk-json "$WORKDIR/inventory.json" --disks "$disk_names" --redundancy dwr1 \
    --script "$WORKDIR/build-pool.sh" --teardown-script "$WORKDIR/teardown.sh"

echo "== 4/6: Building the pool =="
bash "$WORKDIR/build-pool.sh"

echo "== 5/6: Formatting, mounting, and verifying =="
mkfs.ext4 -F /dev/diskweaver-pool/data
mount /dev/diskweaver-pool/data "$MOUNTPOINT"
df -h "$MOUNTPOINT"
echo "e2e-marker" > "$MOUNTPOINT/e2e-marker"
test -f "$MOUNTPOINT/e2e-marker"

echo "== 6/6: Tearing down =="
umount "$MOUNTPOINT"
bash "$WORKDIR/teardown.sh"

echo
echo "E2E TEST PASSED"
