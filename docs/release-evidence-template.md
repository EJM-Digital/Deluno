# Release Evidence Template

Updated: 2026-05-14

Use this template for issue comments on #81, #85, and #78.

```md
## Candidate Validation Summary
Date:
Candidate tag:
Candidate commit:
Tester(s):

### Install/Upgrade/Rollback Matrix (#81)
| Scenario | Result | Evidence | Notes |
| --- | --- | --- | --- |
| Fresh install | PASS/FAIL | <link> | |
| Upgrade from latest 0.1.x | PASS/FAIL | <link> | |
| Failed apply rollback | PASS/FAIL | <link> | |

### Regression Gates (#85)
- `npm run ci:check`: PASS/FAIL
- `dotnet test Deluno.slnx --configuration Release`: PASS/FAIL
- `npm run test:web`: PASS/FAIL

Artifacts:
- CI output:
- Test report:
- Screenshots/logs:

### Backup/Restore Confidence
- Last backup/restore drill result:
- Restore validation notes:

### Live test from a fresh install
- Plan: the dated live test plan in `docs/exec-plans/active/`, e.g. `E2E-live-test-rc28-2026-10-03.md`
- Candidate version and commit:
- Machine state before install: phase 0 of the plan
- Automatic upgrade-hunting proven live, once per media type (#639):

| Phase | Rows passed | Rows failed (issue numbers) | Untested, and why |
| --- | ---: | --- | --- |
| 1 Install | n | | |
| 2 Setup | n | | |
| 3 Title matrix | n | | |
| 4 Behaviour | n | | |
| 5 Every screen | n | | |

- Defects: list each linked issue, severity, resolution, and evidence.
- Live test recommendation: GO / NO-GO

### Decision
- Recommendation: GO / NO-GO
- Blocking issues:
  - 
```
