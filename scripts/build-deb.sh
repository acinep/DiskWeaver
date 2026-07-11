#!/usr/bin/env bash
# Builds an installable diskweaver_<version>_amd64.deb: the daemon
# (self-contained AOT binary), its systemd unit, PAM service file, the
# Cockpit plugin, and the standalone SPA's static files -- packaged the same
# way scripts/wsl-dev/setup-wsl.sh lays (most of) this out by hand for the
# WSL dev loop -- this is the "give it to a real Linux box" path.
#
# Requires both frontends already built (`npm run build` in src/DiskWeaver.Cockpit
# builds both the Cockpit plugin and the standalone SPA -- see its
# esbuild.config.mjs) and dpkg-deb to be available (any Debian/Ubuntu system,
# or `apt-get install dpkg-dev` elsewhere).
#
# Usage:
#   scripts/build-deb.sh [version]
#
# Output: dist/diskweaver_<version>_amd64.deb
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC_DIR="$REPO_ROOT/src"
VERSION="${1:-0.1.0}"
OUT_DIR="$REPO_ROOT/dist"
STAGE_DIR="$(mktemp -d)"
trap 'rm -rf "$STAGE_DIR"' EXIT

COCKPIT_DIST="$SRC_DIR/DiskWeaver.Cockpit/dist"
STANDALONE_DIST="$SRC_DIR/DiskWeaver.Cockpit/standalone/dist"
for d in "$COCKPIT_DIST" "$STANDALONE_DIST"; do
    if [ ! -d "$d" ] || [ -z "$(ls -A "$d" 2>/dev/null)" ]; then
        echo "build-deb: $d is missing/empty -- run 'npm run build' in src/DiskWeaver.Cockpit first" >&2
        exit 1
    fi
done

echo "==> Publishing DiskWeaver.Daemon (AOT, linux-x64)"
DAEMON_DIR="$STAGE_DIR/usr/lib/diskweaver"
mkdir -p "$DAEMON_DIR"
dotnet publish "$SRC_DIR/DiskWeaver.Daemon" \
    -c Release -r linux-x64 --self-contained \
    -o "$DAEMON_DIR"

echo "==> Staging systemd unit"
mkdir -p "$STAGE_DIR/lib/systemd/system"
cp "$REPO_ROOT/packaging/diskweaverd.service" "$STAGE_DIR/lib/systemd/system/diskweaverd.service"

echo "==> Staging PAM service file"
# Lets PamAuthenticator's pam_start("diskweaver", ...) find a real service config on a fresh
# install -- see packaging/pam.d/diskweaver's own comment. Only auth+account is ever checked
# (POST /auth/login), but the standalone SPA's TCP listener itself is still opt-in (see
# docs/deployment.md) -- installing this file doesn't turn it on by itself.
mkdir -p "$STAGE_DIR/etc/pam.d"
cp "$REPO_ROOT/packaging/pam.d/diskweaver" "$STAGE_DIR/etc/pam.d/diskweaver"

echo "==> Staging /etc/default/diskweaverd"
# The systemd unit's EnvironmentFile= -- this is how an admin turns on the standalone SPA
# (DISKWEAVER_HTTP_PORT/_BIND/_WEBROOT) without ever touching the unit file or writing a
# systemd drop-in themselves. See docs/deployment.md.
mkdir -p "$STAGE_DIR/etc/default"
cp "$REPO_ROOT/packaging/default/diskweaverd" "$STAGE_DIR/etc/default/diskweaverd"

echo "==> Staging Cockpit plugin"
# Same package directory layout Cockpit expects at runtime (see
# scripts/wsl-dev/setup-wsl.sh's symlink setup for the dev-loop equivalent of this),
# but here the files are copied for real -- no symlinks in the package.
COCKPIT_PKG_DIR="$STAGE_DIR/usr/share/cockpit/diskweaver"
mkdir -p "$COCKPIT_PKG_DIR/dist"
cp "$SRC_DIR/DiskWeaver.Cockpit/index.html" "$COCKPIT_PKG_DIR/index.html"
cp "$SRC_DIR/DiskWeaver.Cockpit/manifest.json" "$COCKPIT_PKG_DIR/manifest.json"
cp "$COCKPIT_DIST"/* "$COCKPIT_PKG_DIR/dist/"

echo "==> Staging standalone SPA"
# Not served unless /etc/default/diskweaverd's DISKWEAVER_HTTP_PORT/_WEBROOT are uncommented
# (see docs/deployment.md) -- laid down unconditionally so enabling it later is just an edit +
# restart, no re-install needed.
WEBUI_DIR="$STAGE_DIR/usr/share/diskweaver/webui"
mkdir -p "$WEBUI_DIR/dist"
cp "$SRC_DIR/DiskWeaver.Cockpit/standalone/index.html" "$WEBUI_DIR/index.html"
cp "$STANDALONE_DIST"/* "$WEBUI_DIR/dist/"

echo "==> Writing DEBIAN control files"
mkdir -p "$STAGE_DIR/DEBIAN"
sed "s/@VERSION@/$VERSION/" "$REPO_ROOT/packaging/deb/control" > "$STAGE_DIR/DEBIAN/control"
install -m 0755 "$REPO_ROOT/packaging/deb/postinst" "$STAGE_DIR/DEBIAN/postinst"
install -m 0755 "$REPO_ROOT/packaging/deb/prerm" "$STAGE_DIR/DEBIAN/prerm"
# /etc/pam.d/diskweaver and /etc/default/diskweaverd are both user-editable local config --
# marking them conffiles tells dpkg to preserve local edits across upgrades/prompt on conflict,
# instead of silently overwriting them like every other file in this package.
printf '/etc/pam.d/diskweaver\n/etc/default/diskweaverd\n' > "$STAGE_DIR/DEBIAN/conffiles"

echo "==> Building package"
mkdir -p "$OUT_DIR"
DEB_PATH="$OUT_DIR/diskweaver_${VERSION}_amd64.deb"
dpkg-deb --build --root-owner-group "$STAGE_DIR" "$DEB_PATH"

echo "==> Built $DEB_PATH"
