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

### Decision
- Recommendation: GO / NO-GO
- Blocking issues:
  - 
```
