# Deployment

Two separate things, easy to conflate:

- **Dev inner loop** (`scripts/wsl-dev/setup-wsl.sh` + `scripts/wsl-dev/deploy-wsl.sh`):
  fast rebuild/reinstall against a WSL2 instance while iterating. Not meant
  to leave your machine -- see the scripts' own headers, and
  `scripts/wsl-dev/README.md` for the full loop.
- **Real install on a Linux box** (this doc): a `.deb` package someone else
  can `apt install`, with no source checkout, `dotnet`, or `npm` toolchain
  needed on the target machine.

## Building the package

`npm run build` in `src/DiskWeaver.Cockpit` builds both frontends (the
Cockpit plugin and the standalone SPA -- see its `esbuild.config.mjs`).
Needs `dpkg-deb` available too (present on any Debian/Ubuntu box; elsewhere
`apt-get install dpkg-dev`):

```bash
cd src/DiskWeaver.Cockpit && npm ci && npm run build && cd ../..
scripts/build-deb.sh 0.1.0
```

Produces `dist/diskweaver_0.1.0_amd64.deb`. The daemon is published
self-contained AOT for `linux-x64` (see `DiskWeaver.Daemon.csproj`), so the
target host needs no .NET runtime installed -- only the daemon's own
runtime deps: `mdadm`, `lvm2`, `parted`, `util-linux`, `e2fsprogs`, plus
`cockpit-bridge` for the UI. All of these are declared in
`packaging/deb/control` and pulled in automatically by `apt install`.

### Getting a pre-built package instead

`.github/workflows/build.yml` builds this same package on every push, but
that's just a CI artifact -- expires after 90 days, needs GitHub repo
access to download, and is versioned off the CI run number, not a real
release. Push a `v*` tag instead to get a permanent, publicly-downloadable
`.deb` attached to a GitHub Release:

```bash
git tag v0.1.0
git push --tags
```

The workflow derives the package version directly from the tag name
(`v0.1.0` -> `0.1.0`).

## What the package lays down

Same layout `scripts/wsl-dev/setup-wsl.sh` builds by hand for the dev loop,
minus the symlinks (real files, since there's no live-editing a production
install):

- `/usr/lib/diskweaver/` -- the published daemon
- `/lib/systemd/system/diskweaverd.service` -- the unit from
  `packaging/diskweaverd.service` (see that file's comments for why the
  socket is `Group=diskweaver`/`UMask=0007` rather than a plain root
  socket)
- `/etc/pam.d/diskweaver` -- the PAM service file `PamAuthenticator` logs
  in against (`packaging/pam.d/diskweaver`); a dpkg conffile, so local edits
  survive upgrades
- `/usr/share/cockpit/diskweaver/` -- `index.html`, `manifest.json`,
  `dist/` (Cockpit's real package directory, not `~/.local/share/cockpit/`
  -- that path is a per-user dev override, see `docs/cockpit-plugin.md`)
- `/usr/share/diskweaver/webui/` -- the standalone SPA's `index.html` +
  `dist/`, laid down unconditionally but not served by anything until
  `/etc/default/diskweaverd` turns it on (below)
- `/etc/default/diskweaverd` -- the systemd unit's `EnvironmentFile=`; a
  dpkg conffile, ships with everything below commented out

## Enabling the standalone SPA

Off by default -- the daemon only listens on the Unix socket (Cockpit's
transport) unless told otherwise, since the SPA's TCP listener is a new,
separate attack surface (see `Program.cs`'s own comment on
`DISKWEAVER_HTTP_PORT`). Turn it on by editing `/etc/default/diskweaverd`
(installed with everything below already present, just commented out):

```bash
sudo sed -i 's/^#\(DISKWEAVER_HTTP_PORT\|DISKWEAVER_WEBROOT\)=/\1=/' /etc/default/diskweaverd
sudo systemctl restart diskweaverd
```

or just open the file in an editor and uncomment `DISKWEAVER_HTTP_PORT`/
`DISKWEAVER_WEBROOT` yourself, then restart. No systemd unit-file or
drop-in editing needed -- the packaged unit already has
`EnvironmentFile=-/etc/default/diskweaverd` wired in.

`DISKWEAVER_HTTP_BIND` (default `127.0.0.1`, also in that file) controls
what address it binds -- leave it loopback-only unless there's a reverse
proxy terminating TLS in front of it; the daemon itself doesn't do TLS.

## Install / upgrade / remove

```bash
sudo apt install ./diskweaver_0.1.0_amd64.deb
```

`postinst` creates the `diskweaver` group (needed for the Cockpit-bridge
user to reach the daemon's socket -- see `packaging/diskweaverd.service`),
then enables and (re)starts `diskweaverd.service`. A fresh Cockpit login is
needed after the *first* install for new group membership to take effect,
same caveat as the dev loop.

Re-running `apt install ./diskweaver_<new-version>_amd64.deb` upgrades in
place; `postinst` restarts the service on every configure, so the upgrade
always ends up running the new binary.

```bash
sudo apt remove diskweaver
```

stops and disables the service before removing files (`packaging/deb/prerm`).
`/var/lib/diskweaver` (the journal) and `/run/diskweaverd.sock` are managed
by systemd's `StateDirectory=`/`RuntimeDirectory=` and are not touched by
the package itself.

## Not yet covered

- No `.rpm` (Fedora/RHEL) -- only `.deb` today.
- `DiskWeaver.Cli` isn't packaged; it's not yet the thin HTTP client
  described in `docs/daemon-api.md`, so there's nothing useful to ship
  standalone yet.
- No signed/hosted apt repo -- the `.deb` is installed from a local file,
  not `apt-get install diskweaver` from a repository.
