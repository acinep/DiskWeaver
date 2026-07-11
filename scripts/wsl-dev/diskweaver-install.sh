#!/usr/bin/env bash
# Installed to /usr/local/sbin/diskweaver-install.sh, root-owned, by setup-wsl.sh.
# Only root-privileged step in the redeploy loop: stop the service, swap the
# published binary into place, restart it. Runs via a scoped NOPASSWD sudoers
# entry (see setup-wsl.sh) so deploy-wsl.sh can call it non-interactively --
# the source path is hardcoded rather than taken as an argument so the
# sudoers entry can match the exact command line with no argument-injection
# surface.
set -euo pipefail

SRC="/home/noel/diskweaver-publish"
DEST="/usr/lib/diskweaver"

if [ ! -d "$SRC" ]; then
    echo "diskweaver-install: $SRC does not exist -- run 'dotnet publish' first" >&2
    exit 1
fi

systemctl stop diskweaverd.service || true
mkdir -p "$DEST"
rm -rf "${DEST:?}"/*
cp -r "$SRC"/. "$DEST"/
systemctl start diskweaverd.service
systemctl is-active --quiet diskweaverd.service && echo "diskweaverd restarted OK" || {
    echo "diskweaverd failed to start -- check: journalctl -u diskweaverd.service -n 50" >&2
    exit 1
}
