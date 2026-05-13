# Deluno Knowledge Base

Deluno keeps product, architecture, and repo-state context in the repository so agents can inspect and update it directly.

## Start Here

- `../AGENTS.md`: compact agent entry point.
- `ARCHITECTURE.md`: module boundaries, dependency direction, and current architectural posture.
- `QUALITY_SCORE.md`: current quality posture and highest-interest gaps.
- `repo-change-history.md`: commit-by-commit and subsystem-by-subsystem summary of what changed from the last shared baseline.
- `deluno-capability-map.md`: product capability target and current direction.
- `deluno-frontend-backend-map.md`: user-facing IA and backend ownership map.
- `deluno-ui-api-contract.md`: implemented UI-facing API contract and active gaps.
- `external-integration-api.md`: external automation and refine-before-import surface.
- `packaging.md`: source-backed Docker and Windows packaging guide.
- `DEPLOYMENT.md`: current deployment instructions for Docker, Compose, and Windows publish runs.
- `TROUBLESHOOTING.md`: runtime, path-mapping, and startup troubleshooting.

## Execution Plans

- `exec-plans/active/`: checked-in plans for larger in-flight work.
- `exec-plans/completed/`: completed plan history.
- `exec-plans/templates/`: starting points for large feature work and post-merge cleanup.
- `exec-plans/tech-debt-tracker.md`: recurring cleanup queue.

Current note:

- `agent-first-realignment.md` is completed work and now belongs under `exec-plans/completed/`.
- `exec-plans/active/` is intentionally empty apart from `.gitkeep` until another multi-surface plan starts.

## Source Of Truth Rules

- When code and docs disagree, update the docs in the same change or log the drift in `QUALITY_SCORE.md`.
- Core repo maps should describe what is implemented now, not only what was intended in an earlier planning pass.
- Broader subsystem docs are useful orientation material, but agents should trust this index and the linked core docs first.

## Validation

Run:

```powershell
npm.cmd run validate:agents
```

The validation script checks that the agent entry point exists, required docs exist, stale workspace paths are not reintroduced, and high-signal architecture guardrails remain visible.
