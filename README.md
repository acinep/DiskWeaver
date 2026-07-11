# DiskWeaver

Heterogeneous Hybrid RAID (HHR)-style storage pooling for mainline Linux: combine
disks of mixed, arbitrary sizes into a single redundant, expandable pool —
without wasting capacity down to the smallest disk, and without hand-rolling
`mdadm`/LVM. Built on `mdadm` + LVM2 under the hood, driven by a root daemon
that a Cockpit plugin, a standalone web UI, and a CLI all talk to.

See [`docs/PRD.md`](docs/PRD.md) for the full product rationale and
[`docs/algorithm.md`](docs/algorithm.md) for how the tiering/pooling math
actually works.

> [!WARNING]
> **DiskWeaver is pre-1.0 software that partitions disks and creates/
> modifies RAID arrays and LVM volumes.** Used incorrectly, or hit by a
> bug, it can destroy data. Test against disposable loop devices first
> (`docker/e2e-test.sh` or [`docs/testing.md`](docs/testing.md) — neither
> touches a real disk) before pointing it at disks that matter, and keep
> verified backups regardless. See [Project status](#project-status)
> below for what is and isn't validated yet.

## Why DiskWeaver?

Real storage collections are messy — drives retired from a primary NAS,
replaced with larger ones, accumulated over years. The result is a drawer
full of mixed capacities that no standard RAID implementation handles
gracefully. Existing options all fall short in different ways:

| Solution | Mixed sizes | True RAID | Open & recoverable | Platform |
|---|---|---|---|---|
| mdadm / ZFS | ❌ | ✅ | ✅ | Any Linux |
| SnapRAID | ✅ | ❌ | ✅ | Any Linux |
| UnRAID | ✅ | ❌ | ✅ | Proprietary OS |
| Drobo BeyondRAID | ✅ | ✅ | ❌ | Abandoned hardware |
| Synology SHR | ✅ | ✅ | ❌ | Proprietary hardware |
| **DiskWeaver** | ✅ | ✅ | ✅ | Any Linux |

SnapRAID and UnRAID solve the mixed-size problem but at the cost of real
RAID semantics — both are fundamentally parity backup systems, where data
lives on individual drives and parity is used for reconstruction after
failure. A drive failure means that drive's data is gone until you
rebuild, and performance under concurrent access reflects single-drive
speeds, not an array.

Drobo's BeyondRAID got closest to the right answer — genuine redundancy
across mixed drive sizes — and then Drobo collapsed, leaving users with
proprietary hardware, a proprietary on-disk format, and a recovery story
that requires expensive specialist software. It's the best argument for
why "trust us, it works" is not a storage strategy.

Synology's SHR is excellent, and genuinely the inspiration for
DiskWeaver's approach — but it requires Synology hardware, so you can't
run it on a server you already own, and you can't migrate away from it
without rebuilding from scratch.

DiskWeaver does what SHR does, on any Linux system, using standard
`mdadm` and LVM2 under the hood. There's no proprietary on-disk format
and no special recovery tools required — if DiskWeaver disappeared
tomorrow, your pool is still a set of standard mdadm arrays on LVM
volumes that any Linux administrator can work with using tools that ship
with every distribution. The answer to "what happens if DiskWeaver goes
away?" is boring: nothing. Your data is fine, and your tools already
know how to read it.

## Repository layout

All buildable projects live under `src/`; `docs/`, `scripts/`, `packaging/`,
`docker/`, and `.github/` at the root are documentation, dev tooling, and CI.

| Project | What it is |
|---|---|
| `src/DiskWeaver.Core` | Pure domain logic: disk inventory parsing, tiering/pooling planner, and the executor that turns a plan into `mdadm`/`parted`/LVM commands. No UI, no networking. |
| `src/DiskWeaver.Daemon` | The root, systemd-managed service. Owns all disk state, executes plans, and exposes both over HTTP — a Unix socket for Cockpit, and an opt-in TCP listener (PAM-authenticated) for the standalone SPA. See [`docs/daemon-api.md`](docs/daemon-api.md). |
| `src/DiskWeaver.Cockpit` | Two React/PatternFly frontends built from one shared component tree (see its `esbuild.config.mjs`): the Cockpit plugin (`src/app.jsx`) and the standalone SPA (`src/standalone/`), which differ only in how they reach the daemon (`src/api.js` vs `src/api.standalone.js`). See [`docs/cockpit-plugin.md`](docs/cockpit-plugin.md). |
| `src/DiskWeaver.Cli` | Offline, human-in-the-loop tool: plans a pool (from live `lsblk`, a captured JSON snapshot, or synthetic disk sizes) and emits a reviewable shell script rather than executing anything itself. Does not talk to the daemon — see [`docs/daemon-api.md`](docs/daemon-api.md) for the known duplication this implies. |

Each has a matching `*.Tests` project (xUnit).

## Building and testing

Requires the .NET 10 SDK. `DiskWeaver.Daemon` publishes self-contained
NativeAOT for `linux-x64` — that specific step needs a Linux toolchain
(`clang`/`zlib1g-dev`), so on Windows it needs WSL2 (see
[`CONTRIBUTING.md`](CONTRIBUTING.md)); `dotnet build`/`dotnet test`
themselves don't, and run fine on Windows directly.

```bash
dotnet test src/DiskWeaver.slnx -c Release
```

The two frontends (Cockpit plugin + standalone SPA) build together:

```bash
cd src/DiskWeaver.Cockpit && npm ci && npm run build
```

## Developing

`DiskWeaver.Core` and the frontends build and test on any OS with the
.NET 10 SDK and Node. Everything that shells out to `mdadm`/LVM2/PAM
(the daemon, real executor runs) needs a Linux environment — see
[`CONTRIBUTING.md`](CONTRIBUTING.md) for the Linux and Windows (WSL2)
setup paths.

## Running it

- **Local iteration** (real `mdadm`/LVM2/PAM, no packaging step): see
  [`CONTRIBUTING.md`](CONTRIBUTING.md) — native Linux, or WSL2 via
  [`scripts/wsl-dev/README.md`](scripts/wsl-dev/README.md) if you're on
  Windows.
- **Real install** (a `.deb`, systemd unit, PAM service, both frontends):
  see [`docs/deployment.md`](docs/deployment.md).
- **Manual loop-device validation** (no daemon, just the CLI + real
  `mdadm`/`parted` against disposable loop devices): see
  [`docs/testing.md`](docs/testing.md).
- **Automated loop-device e2e test** (the same validation as above,
  scripted end-to-end and disposable): `docker/e2e-test.sh`, runnable
  either inside a `--privileged` Docker container or directly on any
  Linux box (WSL2 included) — no Docker install required either way. Not
  a build for anything you'd ship — it's a `dotnet build`, not the AOT
  daemon publish or the frontends. See [`docker/README.md`](docker/README.md).

## Project status

Pre-1.0. The planner, executor, daemon, both frontends, and the CLI are
built and exercised end-to-end against real loop devices (and real
hardware during development) — see [`docs/execution.md`](docs/execution.md)
for exactly what's been run. That said:

- **Tested platforms:** Ubuntu (CI runs on `ubuntu-latest`; day-to-day
  dev is Ubuntu under WSL2). Packaging is `.deb` only — no `.rpm`, so
  Fedora/RHEL-family aren't currently installable even though the
  planner/executor themselves are distro-agnostic.
- **Supported redundancy:** single-disk fault tolerance (DWR-1: mirror or
  RAID5 depending on disk count) and dual-disk fault tolerance (DWR-2:
  3-way mirror or RAID6). See [`docs/algorithm.md`](docs/algorithm.md).
- **Supported lifecycle operations:** create a pool; expand it by adding
  a disk or replacing one with a larger one, including the RAID-level
  migrations that fall out of that (e.g. mirror → RAID5 → RAID6); detect
  and guide recovery from a degraded/failed disk. All confirmed against
  real hardware or loop devices per [`docs/execution.md`](docs/execution.md).
- **Not supported:**
  - **Shrinking a pool** (removing a disk without replacement).
  - **Importing an existing hand-built mdadm/LVM layout** — DiskWeaver
    only manages pools it created (see `docs/PRD.md` §9).
  - A degraded/failed-pool health dashboard in the UI (`docs/PRD.md`
    §8.4) — degradation is detectable via the API today, just not yet
    surfaced as a dedicated view.
  - An explicit "abort a running execution" endpoint
    (see [`docs/daemon-api.md`](docs/daemon-api.md)).
  - The CLI does not yet talk to the daemon (it emits a reviewable
    script instead), so its behavior can drift from the two web
    frontends — see the `DiskWeaver.Cli` row above.
- **Crash recovery:** every execution step is journaled before/after
  running, and re-running a plan after a crash is idempotent (completed
  steps aren't repeated) — validated by manually killing the daemon
  mid-execution and restarting. Systematic fault-injection (failing
  after *every* individual command and confirming safe recovery or
  explicit manual-recovery instructions) is not yet a standing test —
  it's the main remaining trust gap for anyone running this against
  real data, and the next big investment area.

If you hit a case not covered above, please open an issue — see
[`SECURITY.md`](SECURITY.md) instead if it's a vulnerability rather than
a bug.

## Docs

- [`CONTRIBUTING.md`](CONTRIBUTING.md) — dev environment setup, Linux and Windows/WSL2.
- [`docs/PRD.md`](docs/PRD.md) — product rationale, why HHR-style pooling.
- [`docs/algorithm.md`](docs/algorithm.md) — tiering/pooling math.
- [`docs/execution.md`](docs/execution.md) — plan → commands, journaling, crash recovery.
- [`docs/state-model.md`](docs/state-model.md) — how existing pool state is read back from the host.
- [`docs/daemon-api.md`](docs/daemon-api.md) — the HTTP API, and what backs each endpoint.
- [`docs/cockpit-plugin.md`](docs/cockpit-plugin.md) — the Cockpit plugin's build/serve/debugging notes.
- [`docs/deployment.md`](docs/deployment.md) — packaging and production install.
- [`docs/testing.md`](docs/testing.md) — manual validation against real loop devices.
- [`docs/conventions.md`](docs/conventions.md) — coding conventions.
- [`docker/README.md`](docker/README.md) — the disposable, scripted version of `docs/testing.md`'s e2e test; not a deployment artifact.
