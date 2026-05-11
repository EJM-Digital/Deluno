# Job Status Architecture

This document explains the centralized job status constant system and how to use it correctly.

## Overview

All job status comparisons in the frontend must use the `JOB_STATUS` constants defined in [`src/lib/job-status-constants.ts`](src/lib/job-status-constants.ts). This ensures consistency, type safety, and maintainability across the codebase.

## Job Status Values

There are four job status values:

- **`queued`** - Job is waiting to be processed
- **`running`** - Job is currently executing
- **`completed`** - Job finished successfully
- **`failed`** - Job failed with an error

## Usage

### ✅ Correct Usage

Always import and use the constants:

```typescript
import { JOB_STATUS, isJobActive, type JobStatus } from "../lib/job-status-constants";

// Direct comparisons
if (item.status === JOB_STATUS.RUNNING) {
  // ...
}

// Helper functions for common checks
if (isJobActive(status)) {
  // Status is either queued or running
}

// Filtering
const activeJobs = jobs.filter((job) => isJobActive(job.status as JobStatus));
```

### ❌ Incorrect Usage

Never hardcode status strings:

```typescript
// ❌ DON'T DO THIS
if (item.status === "running") { /* ... */ }
if (job.status === "queued" || job.status === "running") { /* ... */ }
const isCompleted = status === "completed";
```

## Available Helper Functions

The constants module provides typed helper functions:

- `isJobActive(status)` - Returns true if status is queued or running
- `isJobInProgress(status)` - Returns true if status is running
- `isJobPending(status)` - Returns true if status is queued
- `isJobDone(status)` - Returns true if status is completed or failed
- `isJobSuccessful(status)` - Returns true if status is completed
- `isJobFailed(status)` - Returns true if status is failed
- `getJobStatusLabel(status)` - Returns UI-friendly label (e.g., "Queued", "Running")
- `getJobStatusVariant(status)` - Returns UI variant for styling (e.g., "success", "destructive")

## Type Safety

The `JobStatus` type is derived from the constants:

```typescript
type JobStatus = (typeof JOB_STATUS)[keyof typeof JOB_STATUS];
```

Use it when typing job-related data:

```typescript
interface JobItem {
  id: string;
  status: JobStatus;
  // ...
}

function checkJobStatus(status: JobStatus): boolean {
  return isJobActive(status);
}
```

## Validation

Run the validation script to ensure no hardcoded strings are present:

```bash
# Using npm script
npm run validate:job-status

# Using bash script
./scripts/validate-job-status.sh
```

The `prebuilt` hook will run this validation before building.

## Architecture Decisions

### Why Centralize?

1. **Single Source of Truth** - All status values are defined in one place
2. **Type Safety** - TypeScript catches invalid status values at compile time
3. **Consistency** - Ensures uniform UI labels and styling across the app
4. **Maintainability** - Changes to status logic only need to be made once
5. **Discoverability** - Developers can easily find all status-related logic

### When to Use Helpers vs. Direct Comparison

- Use **helpers** when you care about logical groups (active jobs, pending jobs, done jobs)
- Use **direct comparison** when you need exact status value (e.g., showing "Running" text)
- Use **getJobStatusLabel/getJobStatusVariant** for rendering status in the UI

## Examples

### Filtering Active Jobs for Dashboard

```typescript
const runningAutomation = data.automation.filter((item) =>
  isJobActive(item.status as JobStatus) || item.searchRequested
).length;
```

### Rendering Status Badge

```typescript
<Badge variant={getJobStatusVariant(job.status as JobStatus)}>
  {getJobStatusLabel(job.status as JobStatus)}
</Badge>
```

### Conditional Rendering

```typescript
{isJobInProgress(job.status as JobStatus) ? (
  <LoaderCircle className="animate-spin" />
) : (
  <CheckCircle2 />
)}
```

## Refactored Files (Phase 2b)

These files have been refactored to use job status constants:

- `apps/web/src/routes/queue-page.tsx`
- `apps/web/src/routes/activity-page.tsx`
- `apps/web/src/routes/system-page.tsx`
- `apps/web/src/routes/dashboard-page.tsx`
- `apps/web/src/routes/tv-wanted-page.tsx`
- `apps/web/src/routes/movies-wanted-page.tsx`

## Future Work

Phase 2c validation ensures no new hardcoded strings are introduced. The validation scripts should be integrated into the CI/CD pipeline to catch any violations during pull request reviews.
