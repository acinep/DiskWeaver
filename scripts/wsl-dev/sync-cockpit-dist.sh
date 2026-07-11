#!/usr/bin/env bash
# Re-creates per-file symlinks for DiskWeaver.Cockpit/dist/ inside Cockpit's
# package directory. Run this after every `npm run build` in
# DiskWeaver.Cockpit (dist/'s file list changes across builds -- esbuild's
# font assets get content-hashed names -- so a one-time symlink setup goes
# stale; this stays correct by re-scanning dist/ each time).
#
# Individual *files* are symlinked, never the dist/ directory itself: Cockpit's
# package server appears to reject serving through a symlinked subdirectory
# (returns 404) even though it happily follows symlinked individual files --
# see docs/cockpit-plugin.md's "never symlink node_modules" gotcha for the
# related debugging story (a different bug, same underlying lesson: don't
# symlink directories into Cockpit's package tree, only files).
set -euo pipefail

REPO_WIN=/mnt/c/Source/Github/DiskWeaver
DIST_SRC="$REPO_WIN/src/DiskWeaver.Cockpit/dist"
DIST_DEST=~/.local/share/cockpit/diskweaver/dist

if [ ! -d "$DIST_SRC" ]; then
    echo "sync-cockpit-dist: $DIST_SRC does not exist -- run 'npm run build' in src/DiskWeaver.Cockpit first" >&2
    exit 1
fi

mkdir -p "$DIST_DEST"
find "$DIST_DEST" -maxdepth 1 -type l -delete
find "$DIST_SRC" -maxdepth 1 -type f -print0 | while IFS= read -r -d '' f; do
    ln -sfn "$f" "$DIST_DEST/$(basename "$f")"
done

echo "==> Synced $(find "$DIST_SRC" -maxdepth 1 -type f | wc -l) file(s) from $DIST_SRC"
