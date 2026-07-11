#!/usr/bin/env bash
# Rebuilds and installs the DiskWeaver daemon into this WSL2 instance after a
# code change. Run from inside WSL (requires scripts/wsl-dev/setup-wsl.sh to
# have been run once already):
#
#   /mnt/c/Source/Github/DiskWeaver/scripts/wsl-dev/deploy-wsl.sh
#
# Does NOT touch the Cockpit plugin -- that's served live from the
# Windows-mounted checkout (see setup-wsl.sh), so a `npm run build` in
# DiskWeaver.Cockpit plus a browser hard-refresh is all that's ever needed
# there.
set -euo pipefail

REPO_WIN=/mnt/c/Source/Github/DiskWeaver
REPO_NATIVE="$HOME/diskweaver"
PUBLISH_DIR="$HOME/diskweaver-publish"

echo "==> Syncing repo to native WSL filesystem"
# WSL2's drvfs mount can serve a briefly-stale cached directory listing for
# /mnt/c right after a file is created/changed on the Windows side -- a real,
# repeatedly-observed race (not theoretical): a file added moments earlier
# has come up completely missing from $REPO_NATIVE after a single rsync, and
# the lag has varied from "cleared by an immediate second rsync" to "still
# stale several seconds later." A fixed number of blind reruns isn't
# reliable, so instead of guessing a retry count, verify convergence
# directly: rsync, diff source against destination, and only stop once
# they actually match (bailing out loudly if they still don't after a
# generous number of attempts, rather than silently publishing a stale tree).
RSYNC_EXCLUDES=(--exclude 'bin/' --exclude 'obj/' --exclude '.vs/' --exclude 'node_modules/' --exclude 'dist/' --exclude '.git/')
DIFF_EXCLUDES=(-x bin -x obj -x .vs -x node_modules -x dist -x .git)
for attempt in $(seq 1 10); do
    rsync -a --delete "${RSYNC_EXCLUDES[@]}" "$REPO_WIN/" "$REPO_NATIVE/"
    if diff -rq "${DIFF_EXCLUDES[@]}" "$REPO_WIN" "$REPO_NATIVE" > /tmp/deploy-wsl-diff.txt 2>&1; then
        break
    fi
    if [ "$attempt" -eq 10 ]; then
        echo "==> Sync did not converge after 10 attempts -- native checkout still differs from source:" >&2
        cat /tmp/deploy-wsl-diff.txt >&2
        exit 1
    fi
    echo "    (native checkout still stale after rsync -- drvfs cache lag, retrying in 2s...)"
    sleep 2
done

echo "==> Publishing DiskWeaver.Daemon (AOT, linux-x64)"
rm -rf "$PUBLISH_DIR"
dotnet publish "$REPO_NATIVE/src/DiskWeaver.Daemon" \
    -c Release -r linux-x64 --self-contained \
    -o "$PUBLISH_DIR"

echo "==> Installing (stop/copy/start, via scoped sudo)"
sudo /usr/local/sbin/diskweaver-install.sh

echo "==> Done."
