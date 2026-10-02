/**
 * One job opened: the run, why it failed, what it was for, and Dismiss for a failed one.
 * Shared by System's Background jobs and Activity's title jobs, so a job reads the same wherever it opens.
 */
import { Button } from "../ui/button";
import { Drawer, DrawerDanger, DrawerFacts, DrawerFooter, DrawerSection } from "../ui/drawer";
import { formatDateTime, useDisplayPreferences } from "../../lib/display-preferences";
import { getJobStatusLabel, isJobFailed, type JobStatus } from "../../lib/job-status-constants";
import { isChosenOnSearch, relatedEntityLabel } from "../../lib/dispatch-target";
import type { JobQueueItem } from "../../lib/api";

function prettyJson(value: string) {
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

export function JobDrawer({ job, busy, onClose, onDismiss }: { job: JobQueueItem | null; busy: boolean; onClose: () => void; onDismiss: (job: JobQueueItem) => void }) {
  const { preferences } = useDisplayPreferences();
  return (
    <Drawer
      open={job !== null}
      onOpenChange={(next) => {
        if (!next) onClose();
      }}
      title={job ? job.label : "Job"}
      description={job ? `${job.sourceLabel} · ${getJobStatusLabel(job.status as JobStatus)}` : undefined}
      footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={onClose} />}
    >
      {job ? (
        <>
          <DrawerSection title="Run" aside={getJobStatusLabel(job.status as JobStatus)}>
            <DrawerFacts
              items={[
                { label: "Attempts", value: String(job.attempts) },
                { label: "Scheduled", value: formatDateTime(job.scheduledUtc, preferences) },
                { label: "Started", value: job.startedUtc ? formatDateTime(job.startedUtc, preferences) : "Not started" },
                { label: "Finished", value: job.completedUtc ? formatDateTime(job.completedUtc, preferences) : "Not finished" },
                { label: "Worker", value: job.workerId ?? "—", mono: true }
              ]}
            />
          </DrawerSection>

          {job.lastError ? (
            <DrawerSection title="Why it failed">
              <p className="whitespace-pre-wrap text-[length:var(--type-body-sm)] leading-relaxed text-destructive">{job.lastError}</p>
            </DrawerSection>
          ) : null}

          {job.relatedEntityId ? (
            <DrawerSection title="What it was for">
              <DrawerFacts
                items={[
                  { label: "Type", value: relatedEntityLabel(job.relatedEntityType, job.relatedEntityId).primary },
                  ...(isChosenOnSearch(job.relatedEntityType) ? [] : [{ label: "Id", value: job.relatedEntityId, mono: true }])
                ]}
              />
            </DrawerSection>
          ) : null}

          {job.payloadJson ? (
            <DrawerSection title="Payload">
              <pre className="overflow-x-auto rounded-[10px] border border-hairline bg-surface-1 p-3 font-mono text-[length:var(--type-caption)] text-muted-foreground">
                {prettyJson(job.payloadJson)}
              </pre>
            </DrawerSection>
          ) : null}

          {isJobFailed(job.status as JobStatus) ? (
            <DrawerSection title="If it does not need doing">
              <DrawerDanger
                title="Dismiss this job"
                description="Deluno stops counting it as failed. What it was and why it failed stays in Events."
                action={
                  <Button type="button" variant="outline" size="sm" onClick={() => onDismiss(job)} disabled={busy}>
                    Dismiss
                  </Button>
                }
              />
            </DrawerSection>
          ) : null}
        </>
      ) : null}
    </Drawer>
  );
}
