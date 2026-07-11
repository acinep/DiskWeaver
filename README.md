# DiskWeaver

Heterogeneous Hybrid RAID (HHR)-style storage pooling for mainline Linux: combine
disks of mixed, arbitrary sizes into a single redundant, expandable pool —
without wasting capacity down to the smallest disk, and without hand-rolling
`mdadm`/LVM. Built on `mdadm` + LVM2 under the hood, driven by a root daemon
that a Cockpit plugin, a standalone web UI, and a CLI all talk to.

See [`docs/PRD.md`](docs/PRD.md) for the full product rationale and
[`docs/algorithm.md`](docs/algorithm.md) for how the tiering/pooling math
actually works.

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
NativeAOT for `linux-x64` — Linux + `clang`/`zlib1g-dev` are needed for that
specific step, not for `dotnet build`/`dotnet test` themselves.

```bash
dotnet test src/DiskWeaver.slnx -c Release
```

The two frontends (Cockpit plugin + standalone SPA) build together:

```bash
cd src/DiskWeaver.Cockpit && npm ci && npm run build
```

## Running it

- **Local iteration** (WSL2 + real `mdadm`/LVM2/PAM, no packaging step):
  see [`scripts/wsl-dev/README.md`](scripts/wsl-dev/README.md).
- **Real install** (a `.deb`, systemd unit, PAM service, both frontends):
  see [`docs/deployment.md`](docs/deployment.md).
- **Manual loop-device validation** (no daemon, just the CLI + real
  `mdadm`/`parted` against disposable loop devices): see
  [`docs/testing.md`](docs/testing.md).

## Docs

- [`docs/PRD.md`](docs/PRD.md) — product rationale, why HHR-style pooling.
- [`docs/algorithm.md`](docs/algorithm.md) — tiering/pooling math.
- [`docs/execution.md`](docs/execution.md) — plan → commands, journaling, crash recovery.
- [`docs/state-model.md`](docs/state-model.md) — how existing pool state is read back from the host.
- [`docs/daemon-api.md`](docs/daemon-api.md) — the HTTP API, and what backs each endpoint.
- [`docs/cockpit-plugin.md`](docs/cockpit-plugin.md) — the Cockpit plugin's build/serve/debugging notes.
- [`docs/deployment.md`](docs/deployment.md) — packaging and production install.
- [`docs/testing.md`](docs/testing.md) — manual validation against real loop devices.
- [`docs/conventions.md`](docs/conventions.md) — coding conventions.
