# Deluno Knowledge Base

Deluno keeps product and engineering context in the repository so agents can inspect, validate, and update it directly.

## Start Here

- `../AGENTS.md`: compact agent entry point.
- `ARCHITECTURE.md`: module boundaries, dependency direction, and enforcement rules.
- `QUALITY_SCORE.md`: current quality posture and gap tracker.
- `deluno-capability-map.md`: product capability target.
- `deluno-product-map.md`: user-facing product shape.
- `deluno-ui-api-contract.md`: frontend/backend data contracts.
- `external-integration-api.md`: external automation and integration surface.

## Execution Plans

- `exec-plans/active/`: checked-in plans for larger work.
- `exec-plans/completed/`: completed plan history.
- `exec-plans/tech-debt-tracker.md`: recurring cleanup queue.

Small changes can use an in-thread plan. Multi-surface work should create or update an execution plan with status, decisions, and validation notes.

## Generated And Derived Maps

The existing `deluno-*map.md` and `*-strategy.md` files are useful orientation material. When code and docs disagree, update the docs in the same change or record the drift in `QUALITY_SCORE.md`.

## Validation

Run:

```powershell
npm.cmd run validate:agents
```

The validation script checks that the agent entry point exists, required docs exist, stale workspace paths are not reintroduced, and key architectural boundaries remain mechanically visible.
