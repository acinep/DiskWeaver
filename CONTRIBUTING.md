# Contributing to DiskWeaver

## What needs Linux, and what doesn't

- **`DiskWeaver.Core`** (inventory parsing, the tiering planner, the
  executor's command generation) is pure, platform-independent C# — no
  disk access, no shelling out. Builds and tests on Windows, macOS, or
  Linux with just the .NET 10 SDK.
- **`DiskWeaver.Cli`** and **`DiskWeaver.Daemon`** shell out to
  `mdadm`/`parted`/LVM2/PAM. You can build them anywhere, but *running*
  them for real — creating an actual array, exercising the daemon against
  a real disk or loop device — needs a real Linux kernel.
- **`DiskWeaver.Cockpit`** (the Cockpit plugin + standalone SPA, one
  shared React/PatternFly tree) is plain Node/esbuild — builds anywhere
  Node runs. Seeing it do anything needs a daemon to talk to, which loops
  back to the point above.

So: `dotnet test` and reading/reviewing code need nothing special. Once
you're touching the executor, the daemon, or want to see either frontend
against live state, you need a Linux environment somewhere — a real Linux
box, a VM, or (if you're on Windows) WSL2.

## Building and running the test suite (any OS)

```bash
dotnet test src/DiskWeaver.slnx -c Release
```

```bash
cd src/DiskWeaver.Cockpit && npm ci && npm run build
```

Both work identically on Windows, macOS, or Linux. This is enough for
most `DiskWeaver.Core` and planner/algorithm work — no Linux environment
required.

## If you're on Linux

You don't need `scripts/wsl-dev/setup-wsl.sh` or `deploy-wsl.sh` at all.
Both exist purely to work around the Windows↔WSL2 boundary — rsyncing
the repo off the 9p-mounted checkout into WSL2's native filesystem, then
publishing/installing the daemon from that copy. On a native Linux box
there's no boundary to cross: you're developing directly in the checkout,
so `dotnet publish` + `cp`/`systemctl restart` (the same two steps
`deploy-wsl.sh` runs, minus the rsync) is all a redeploy needs.

**One-time dependency install:**

```bash
sudo apt-get install -y mdadm lvm2 parted util-linux e2fsprogs jq \
    dotnet-sdk-10.0 dotnet-sdk-aot-10.0 clang zlib1g-dev
```

**Loop-device validation (no daemon, no install step)** — the fastest
inner loop for planner/executor changes, and fully scripted:

```bash
sudo docker/e2e-test.sh
```

Despite the directory name this needs no Docker — it's plain bash +
`dotnet` against real loop devices. See
[`docker/README.md`](docker/README.md) (Option A) and
[`docs/testing.md`](docs/testing.md) for what it does and doesn't
validate, and for running the same steps by hand instead of via the
script.

**Running the actual daemon + a frontend against it** — there's no
ready-made script for native Linux yet (only the WSL2 ones in
`scripts/wsl-dev/`, described below), but the steps are the same minus
the Windows-mount workarounds:

1. Install the systemd unit: `sudo cp packaging/diskweaverd.service /etc/systemd/system/ && sudo systemctl daemon-reload`
2. `sudo groupadd -f diskweaver && sudo usermod -aG diskweaver "$(whoami)"` (new login session needed to take effect), then publish and copy the daemon into place — see `scripts/wsl-dev/diskweaver-install.sh` for the exact `dotnet publish` invocation and target paths, which are OS-agnostic.
3. Point Cockpit at your checkout directly (`ln -s` the plugin's `index.html`/`manifest.json`/`dist/` into `~/.local/share/cockpit/diskweaver/` — see the "never symlink node_modules" gotcha in [`docs/cockpit-plugin.md`](docs/cockpit-plugin.md) before symlinking the whole `DiskWeaver.Cockpit/` directory), or run the standalone SPA by enabling `DISKWEAVER_HTTP_PORT` per [`docs/deployment.md`](docs/deployment.md#enabling-the-standalone-spa).

A native-Linux equivalent of `scripts/wsl-dev/deploy-wsl.sh` (skipping
the rsync-off-9p-mount step, since there's no mount to work around) would
be a welcome contribution if you find yourself doing this repeatedly.

**Building a `.deb`:** see [`docs/deployment.md`](docs/deployment.md) —
this always needs Linux (`dpkg-deb`), on any platform.

## If you're on Windows

Everything that needs Linux goes through **WSL2**. This is how the
maintainer develops day to day, so it's the most exercised, best-documented
path — start with [`scripts/wsl-dev/README.md`](scripts/wsl-dev/README.md):

```powershell
wsl -d Ubuntu
/mnt/c/path/to/DiskWeaver/scripts/wsl-dev/setup-wsl.sh   # one-time
scripts/wsl-dev/deploy-wsl.sh                             # after every daemon change
```

This syncs the repo into WSL2's native filesystem (loop-device-backed
`mdadm`/LVM2 work is unreliable over the Windows-mounted 9p filesystem —
see [`docker/README.md`](docker/README.md)), builds/installs the daemon,
sets up a scoped passwordless `sudo` rule for redeploys, and symlinks the
Cockpit plugin straight at your Windows-mounted checkout so `npm run
build` alone picks up on next page load — no reinstall step for
frontend-only changes.

For the same loop-device validation described above (no daemon, fastest
inner loop for planner/executor work), the same `docker/e2e-test.sh`
script works from inside that same WSL2 shell — see
[`docker/README.md`](docker/README.md) (Option A), or Option B if you'd
rather go through Docker Desktop instead of a bare WSL2 distro.

`.deb` packaging (`docs/deployment.md`) also just works from inside
WSL2 — it's a normal Linux build step once you're in that shell.

## Coding conventions

See [`docs/conventions.md`](docs/conventions.md).
