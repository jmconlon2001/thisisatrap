# Build progress

Working branch: `feature/mailtrap-parity`. Plan: closing the gap with mailtrap.io. Each stage is committed and pushed when it's green.

| # | Stage | Status |
| --- | --- | --- |
| 0 | Docker support | Done |
| 1 | Test projects, coverage gate, git hooks, CI | Done |
| 2 | Rebrand to **ThisIsATrap** (UI, code, config, Docker, IIS) | Next |
| 3 | Multiple inboxes | |
| 4 | Responsive preview | |
| 5 | HTML check (caniemail) | |
| 6 | Spam & content analysis | |
| 7 | Link & image checker | |
| 8 | STARTTLS / SMTPS | |
| 9 | Webhooks & error simulation | |
| 10 | Security audit & hardening | |
| 11 | README rewrite with screenshots, redeploy IIS (+ Docker), open PR | |

## Resuming after a pause
1. `git log --oneline -5` shows the last stage that was pushed.
2. Find the first stage in the table above that isn't marked Done and continue from it.
3. Each stage needs `./tools/Test-Coverage.ps1` passing before it's committed.

## Notes
- Tests run on Microsoft.Testing.Platform (`global.json` → `test.runner`). Unit tests: `dotnet test --project tests/MailTrap.UnitTests`. Everything plus coverage: `./tools/Test-Coverage.ps1`.
- Hooks: run `./tools/Install-GitHooks.ps1` once per clone.
