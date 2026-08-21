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

### 14-day Soak (#82)
- Run ID:
- Candidate commit:
- Day 0 baseline and backup:
- Daily evidence: `artifacts/soak/<run-id>/daily.md`

| Day | Date | Ready | Critical alerts | Jobs failed | API error % | Free storage % | Workflow/filesystem notes | Result |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- | --- |
| 0 | YYYY-MM-DD | 1/0 | 0 | n | n | n | Baseline and backup ID | PASS/FAIL |
| 1–14 | YYYY-MM-DD | 1/0 | 0 | n | n | n | Link to daily snapshot and operator notes | PASS/FAIL |

- Defects: list each linked issue, severity, resolution, and evidence.
- Soak recommendation: GO / NO-GO

### Decision
- Recommendation: GO / NO-GO
- Blocking issues:
  - 
```
