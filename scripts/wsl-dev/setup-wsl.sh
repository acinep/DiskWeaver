#!/usr/bin/env bash
# One-time (idempotent) setup for developing/running DiskWeaver against a
# WSL2 Ubuntu instance. Run this once per fresh WSL distro from *inside* WSL:
#
#   /mnt/c/Source/Github/DiskWeaver/scripts/wsl-dev/setup-wsl.sh
#
# After this, use scripts/wsl-dev/deploy-wsl.sh to build+install the daemon after
# every code change. The Cockpit plugin needs no install step at all -- it's
# symlinked straight at the Windows-mounted checkout (see below), so
# `npm run build` alone (from either side) is enough; Cockpit picks up the
# new dist/ output on next page load.
#
# Assumes: Ubuntu on WSL2, repo checked out at
# /mnt/c/Source/Github/DiskWeaver (adjust REPO_WIN below if that's wrong),
# Cockpit already installed and cockpit.socket already running (this repo's
# WSL images have shipped with it preinstalled; if not, `apt-get install
# cockpit` first).
#
# Run this as your normal login user, NOT via `sudo` -- it escalates itself
# per-command below. TARGET_USER exists specifically to survive it being
# run under sudo anyway: plain `$(whoami)` would then resolve to "root",
# silently granting the diskweaver group and the scoped NOPASSWD sudo rule
# below to root (a no-op, since root already has both) instead of the real
# login user -- exactly the bug that once left deploy-wsl.sh unable to
# redeploy non-interactively despite this script having "succeeded".
set -euo pipefail

REPO_WIN=/mnt/c/Source/Github/DiskWeaver
REPO_NATIVE="$HOME/diskweaver"
TARGET_USER="${SUDO_USER:-$(id -un)}"

echo "==> Installing daemon build/runtime dependencies"
sudo apt-get update
sudo apt-get install -y \
    mdadm lvm2 parted util-linux e2fsprogs jq \
    dotnet-sdk-10.0 dotnet-sdk-aot-10.0 clang zlib1g-dev \
    rsync

echo "==> Syncing repo to native WSL filesystem ($REPO_NATIVE)"
# Loop-device-backed mdadm/lvm2 work is unreliable on the Windows-mounted
# 9p filesystem (see docker/README.md) -- the daemon is always built and run
# from this native copy, kept in sync by deploy-wsl.sh on every redeploy.
mkdir -p "$REPO_NATIVE"
rsync -a --delete \
    --exclude 'bin/' --exclude 'obj/' --exclude '.vs/' \
    --exclude 'node_modules/' --exclude 'dist/' --exclude '.git/' \
    "$REPO_WIN/" "$REPO_NATIVE/"

echo "==> Symlinking Cockpit plugin (served live from the Windows-mounted checkout)"
# Link only the files Cockpit actually needs to serve (index.html, manifest.json,
# dist/) into their own directory -- NOT the whole DiskWeaver.Cockpit folder.
# That folder also contains node_modules/ (tens of thousands of files from the
# esbuild/React/PatternFly toolchain) and src/; symlinking the whole thing put
# node_modules inside Cockpit's package directory, where cockpit-bridge appears
# to do a recursive scan of the package tree on session start. Over the
# Windows-mounted (9p) filesystem, that scan across 50k+ small files reliably
# stalled the entire bridge session for ~35-45s -- indistinguishable from a
# broken WSL/Cockpit install (see docs/cockpit-plugin.md's Known gotcha
# section for the debugging story) until isolated down to this.
COCKPIT_PKG_DIR=~/.local/share/cockpit/diskweaver
mkdir -p "$COCKPIT_PKG_DIR"
ln -sfn "$REPO_WIN/src/DiskWeaver.Cockpit/index.html" "$COCKPIT_PKG_DIR/index.html"
ln -sfn "$REPO_WIN/src/DiskWeaver.Cockpit/manifest.json" "$COCKPIT_PKG_DIR/manifest.json"
# dist/ itself is never symlinked as a directory -- see
# scripts/wsl-dev/sync-cockpit-dist.sh for why (Cockpit's package server appears to
# reject serving through a symlinked subdirectory). Re-run that script after
# every `npm run build`.
"$REPO_NATIVE/scripts/wsl-dev/sync-cockpit-dist.sh"

echo "==> Creating diskweaver group and adding $TARGET_USER to it"
# The daemon's Unix socket is group-owned "diskweaver" with mode 0660 (see
# packaging/diskweaverd.service) so cockpit-bridge -- which runs as this
# login user, not root -- can connect without the socket being world-writable.
# Group membership only takes effect for *new* login sessions, so a fresh
# Cockpit login (or `wsl --shutdown` + reopen) is needed after this runs for
# the first time.
sudo groupadd -f diskweaver
sudo usermod -aG diskweaver "$TARGET_USER"

echo "==> Installing systemd unit"
sudo cp "$REPO_NATIVE/packaging/diskweaverd.service" /etc/systemd/system/diskweaverd.service
sudo systemctl daemon-reload
sudo systemctl enable diskweaverd.service
sudo systemctl restart diskweaverd.service

echo "==> Installing privileged redeploy helper to /usr/local/sbin"
sudo install -o root -g root -m 0755 \
    "$REPO_NATIVE/scripts/wsl-dev/diskweaver-install.sh" /usr/local/sbin/diskweaver-install.sh

echo "==> Granting scoped passwordless sudo for the redeploy helper only"
SUDOERS_FILE=/etc/sudoers.d/diskweaver-dev
echo "$TARGET_USER ALL=(root) NOPASSWD: /usr/local/sbin/diskweaver-install.sh" | sudo tee "$SUDOERS_FILE" >/dev/null
sudo chmod 0440 "$SUDOERS_FILE"
sudo visudo -c -f "$SUDOERS_FILE"

echo "==> Done. Next: scripts/wsl-dev/deploy-wsl.sh to build and install the daemon."
