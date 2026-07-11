## What and why

<!-- What does this change, and what problem does it solve? -->

## How was this tested

<!--
- `dotnet test src/DiskWeaver.slnx -c Release`?
- `docker/e2e-test.sh` or manual loop-device validation (docs/testing.md)?
- Against real hardware?
- UI-only: which frontend, what did you click through?
-->

## Checklist

- [ ] I read [CONTRIBUTING.md](../CONTRIBUTING.md)
- [ ] Tests pass locally (`dotnet test src/DiskWeaver.slnx -c Release`)
- [ ] If this touches command generation/execution
      (`DiskWeaver.Core`'s `CommandPlanner`/executor, or the daemon),
      I validated it against real loop devices or hardware, not just
      unit tests — see [docs/testing.md](../docs/testing.md)
- [ ] Docs updated if this changes behavior described in `docs/`
