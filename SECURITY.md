# Security Policy

DiskWeaver's daemon (`diskweaverd`) runs as root and executes `mdadm`/
`parted`/LVM2 commands against real block devices, and its TCP listener
authenticates over PAM. Vulnerabilities here range from data loss to
local privilege escalation — please report them privately rather than
through a public issue.

## Reporting a vulnerability

Preferred: use GitHub's [private vulnerability
reporting](../../security/advisories/new) for this repository (Security
tab → "Report a vulnerability"). This opens a private advisory visible
only to maintainers until a fix is ready.

If you can't use that, email **noel.phillips@gmail.com** with a
description of the issue, steps to reproduce, and its potential impact.

Please don't open a public issue or PR for a suspected vulnerability
before it's been triaged.

## What's in scope

- The daemon's HTTP API (Unix socket and the opt-in PAM-authenticated
  TCP listener) — auth bypass, command injection, path traversal, or any
  way to make it act on disks/paths outside what was requested.
- The generated `mdadm`/`parted`/LVM command sequences — anything that
  could corrupt data or an array beyond what the reviewed plan describes.
- The Cockpit plugin / standalone SPA / CLI, where a flaw could be used
  to trick a user into approving a different action than the one shown.

## What to expect

This is a small, unfunded open-source project — there's no formal SLA,
but security reports get priority over other work. Expect an initial
response within a few days.
