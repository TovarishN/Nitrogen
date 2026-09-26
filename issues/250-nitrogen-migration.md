# 250 — Migrate Nitrogen milestone records

Status: Completed and verified

Branch: `codex/250-nitrogen-migration` from standalone `main`.

Move the conversation's Nitrogen roadmap, approved designs and plans, and issue records into the extracted standalone repository. Confirm the milestone code matches Gravity, keep the standalone solution independent, and run its build and tests. Gravity-only editor adapter and Motion/Policy integration remain in Gravity.

## Log

- 2026-09-26 — Gravity milestone branches 244–249 were merged locally into `master`. The extracted Nitrogen repository already had byte-identical core milestone code. Copied the 13 roadmap/design/plan files and six issue records into this repository.
- 2026-09-26 — Standalone `dotnet build Nitrogen.slnx` succeeded with four existing warnings; `dotnet test Nitrogen.Tests/Nitrogen.Tests.csproj --no-build` passed 606/606. The standalone test project excludes Gravity-dependent integration tests; those remain in Gravity.
