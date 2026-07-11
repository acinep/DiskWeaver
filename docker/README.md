# E2E test harness (`docker/e2e-test.sh`)

Runs the full loop-device validation workflow from
[../docs/testing.md](../docs/testing.md) — testkit → plan → build →
mkfs/mount → verify → teardown — as one non-interactive script, against
real `mdadm`/`parted`/`lvm2`. This is the same loop-device approach used
for manual hardware validation, just automated and disposable so it can
be re-run repeatedly (e.g. after every change to `CommandPlanner`)
instead of by hand. It's confirmed working end-to-end both ways below.

Despite living in `docker/`, the script has no Docker-specific commands
in it — it's plain bash + `dotnet` + standard Linux tools. Docker is one
way to get a disposable Linux environment to run it in; **WSL2 directly
is another, and doesn't require installing Docker at all.**

## Option A: WSL2 directly (no Docker install needed)

If you're on Windows with WSL2 already available (`wsl -l -v` to check),
this is the lower-friction path:

```powershell
wsl -d Ubuntu
```

Inside that shell, one-time setup:

```bash
sudo apt-get update
sudo apt-get install -y mdadm lvm2 parted util-linux e2fsprogs jq dotnet-sdk-10.0
```

Copy the repo into WSL2's *native* filesystem rather than running it
from `/mnt/c/...` — loop devices backed by files on the Windows-mounted
drive can behave unreliably:

```bash
rsync -a --exclude='bin/' --exclude='obj/' --exclude='.vs/' /mnt/c/path/to/DiskWeaver/ ~/diskweaver/
cd ~/diskweaver
dotnet build src/DiskWeaver.slnx -c Release
sudo docker/e2e-test.sh
```

Expect `E2E TEST PASSED` at the end.

## Option B: Docker

Needs `--privileged`: loop devices, `mdadm`, and LVM all need real kernel
access that an unprivileged container doesn't get. On Windows with Docker
Desktop, Linux containers run inside its own WSL2/Hyper-V VM, and
`--privileged` works the same way there as on a native Linux Docker host.

```bash
docker build -t diskweaver-e2e -f docker/Dockerfile .
docker run --rm --privileged diskweaver-e2e
```

For faster inner-loop iteration on `docker/e2e-test.sh` without a full
rebuild, run the image interactively instead:

```bash
docker run --rm -it --privileged --entrypoint bash diskweaver-e2e
# inside the container:
docker/e2e-test.sh
```

## What this validates vs. doesn't

Same scope either way: confirms `CommandPlanner`'s generated commands
are actually accepted by real `mdadm`/`parted`/LVM/ext4, end to end.
Doesn't validate real disk timing (rebuild/reshape duration on large
real arrays). Phase 2b (journaling, crash recovery, automated
invocation from the daemon) and Phase 4 (Cockpit UI) are now built and
validated separately — via the daemon running directly in WSL2 and a
real Cockpit session, not through this Docker/script harness — see
../docs/execution.md, ../docs/daemon-api.md, and
../docs/cockpit-plugin.md.

## Known-harmless noise

Both environments can print things that look alarming but aren't bugs:
`parted`'s "not properly aligned for best performance" warning, and
"File descriptor 7 (/dev/ptmx) leaked on ... invocation" from LVM tools
under WSL2/some ttys. Neither affects correctness — the operations
still succeed.
