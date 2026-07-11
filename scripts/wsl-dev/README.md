# WSL2 dev loop

Fast local iteration against a real Linux daemon (mdadm/lvm2/parted, PAM,
systemd) without leaving Windows. Not meant to produce anything you'd ship
-- for that, see `scripts/build-deb.sh` and `docs/deployment.md`.

## One-time setup

From inside a WSL2 Ubuntu shell (`wsl -d Ubuntu`), with this repo checked
out on the Windows side at `/mnt/c/Source/Github/DiskWeaver`:

```bash
/mnt/c/Source/Github/DiskWeaver/scripts/wsl-dev/setup-wsl.sh
```

Idempotent -- safe to re-run. **Run it as yourself, not via `sudo`** (it
escalates itself per-command); see the script's own header for why running
the whole thing under `sudo` silently breaks the group membership and
sudoers rule it sets up. Installs build/runtime dependencies, syncs the
repo to a native-filesystem copy (loop-device-backed mdadm/lvm2 work is
unreliable over the Windows-mounted 9p mount -- see `docker/README.md`),
symlinks the Cockpit plugin straight at the Windows-mounted checkout,
creates the `diskweaver` group, installs the systemd unit, and grants a
scoped passwordless `sudo` rule for the one redeploy helper below.

A fresh Cockpit login (or `wsl --shutdown` + reopen) is needed after the
first run for the new group membership to take effect.

## Every code change

**Daemon (C#):**

```bash
scripts/wsl-dev/deploy-wsl.sh
```

Re-syncs the repo, publishes the daemon (AOT, `linux-x64`), and installs +
restarts it via the scoped sudo helper (`diskweaver-install.sh`, installed
to `/usr/local/sbin` by setup, root-owned) -- the only privileged step, and
the only reason the sudoers rule from setup exists.

**Cockpit plugin / standalone SPA (JS):**

```bash
cd src/DiskWeaver.Cockpit && npm run build
```

then, the first time only, re-sync the per-file symlinks (`dist/`'s file
list changes across builds -- esbuild's font assets get content-hashed
names):

```bash
scripts/wsl-dev/sync-cockpit-dist.sh
```

No install step after that -- both frontends are served straight from the
Windows-mounted checkout. A browser hard-refresh picks up the new build for
the standalone SPA; Cockpit picks it up on next page load.

## Files here

- `setup-wsl.sh` -- one-time idempotent setup (above).
- `deploy-wsl.sh` -- daemon rebuild+install, run after every C# change.
- `diskweaver-install.sh` -- installed to `/usr/local/sbin`, root-owned;
  the actual stop/copy/start step `deploy-wsl.sh` calls via scoped sudo.
  Not meant to be run directly from here.
- `sync-cockpit-dist.sh` -- re-creates the Cockpit plugin's dist/ symlinks;
  run after `npm run build` changes the file list.

## Troubleshooting

- **`deploy-wsl.sh` prompts for a password / "sudo: interactive
  authentication is required"**: the scoped NOPASSWD sudoers rule isn't
  matching. Check `sudo cat /etc/sudoers.d/diskweaver-dev` -- it should
  read `<your-username> ALL=(root) NOPASSWD: /usr/local/sbin/diskweaver-install.sh`.
  If it says `root` instead of your username, `setup-wsl.sh` was run via
  `sudo` at some point (see its header comment); re-run it as yourself to
  fix, or just rewrite that one line directly.
- **Cockpit session stalls for ~30-45s on login**: see
  `docs/cockpit-plugin.md`'s "never symlink node_modules" gotcha --
  `setup-wsl.sh` already avoids this by symlinking individual files, not
  the whole `DiskWeaver.Cockpit` directory.
