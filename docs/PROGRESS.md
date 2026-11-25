# Build progress

Working branch: `feature/mailtrap-parity`. The plan adds the features ThisIsATrap was missing compared with mailtrap.io. Each stage is committed and pushed once it's green.

| # | Stage | Status |
| --- | --- | --- |
| 0 | Docker support | Done |
| 1 | Test projects, coverage gate, git hooks, CI | Done |
| 2 | Rebrand to **ThisIsATrap** (UI, code, config, Docker, IIS; legacy `MailTrap` config, database and IIS site are migrated) | Done |
| 3 | Health and status: `/healthz`, `/readyz`, `/api/status`, a status page, Docker and IIS health checks | Done |
| 4 | Multiple inboxes | Done |
| 5 | Responsive preview | Done |
| 6 | HTML check (caniemail) | Done |
| 7 | Spam & content analysis | Next |
| 8 | Link & image checker | |
| 9 | STARTTLS / SMTPS | |
| 10 | Webhooks & error simulation | |
| 11 | Security audit & hardening | |
| 12 | README rewrite with lots of screenshots, redeploy IIS (+ Docker), open PR | |

## Resuming after a pause
1. Run `git log --oneline -5` to see the last stage that was pushed.
2. Continue with the first stage in the table above that isn't marked Done.
3. Before committing a stage, `./tools/Test-Coverage.ps1` must pass.

## Notes
- Stages 0–2 were merged to `main` in PR #1 (at your request). Later stages continue on the branch.
- Tests run on Microsoft.Testing.Platform, set by `test.runner` in `global.json`.
  - Unit tests only: `dotnet test --project tests/ThisIsATrap.UnitTests`
  - Everything with coverage: `./tools/Test-Coverage.ps1`
- Git hooks: run `./tools/Install-GitHooks.ps1` once per clone.
- Rebrand compatibility:
  - The `MailTrap` config section and `MailTrap__*` env vars are still read, and `ThisIsATrap` wins when both set a value.
  - On startup the app renames `mailtrap.db` to `thisisatrap.db`.
  - `Deploy-ThisIsATrap.ps1` removes the old `MailTrap` IIS site and pool and moves the data from `C:\inetpub\MailTrapData`.
- The IIS instance on this server still runs the old `MailTrap` build. It gets redeployed in stage 12.
