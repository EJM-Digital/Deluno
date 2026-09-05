/**
 * How often Deluno checks, and how long it keeps what it takes.
 *
 * <p>The third section DESIGN-007 asked the failure and blocklist console to
 * carry: <i>"the schedules — how often the file check runs, and the recycle
 * bin's retention"</i>. The other two sections answer <em>what</em> Deluno does
 * about a failure; this one answers how often it looks and how long you have to
 * change your mind.</p>
 *
 * <p>The file check had been declared configurable since the day it was written
 * and was not: the System screen printed "6h · configured" beside it while
 * nothing configured anything. This is that setting, and the scheduler now
 * records the cadence it actually claimed at, so the screen reports what is
 * happening rather than what was declared.</p>
 *
 * Contract: PATCH /api/settings, PUT /api/recycle-bin/settings.
 */
import { useState } from "react";
import type { PlatformSettingsSnapshot, RecycleBinSettings } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { toast } from "../shell/toaster";
import { Field } from "../ui/field";
import { PresetField } from "../ui/preset-field";

export interface FailureSchedulesConsoleProps {
  settings: PlatformSettingsSnapshot;
  recycleBin: RecycleBinSettings;
  onChanged: () => void;
}

export function FailureSchedulesConsole({ settings, recycleBin, onChanged }: FailureSchedulesConsoleProps) {
  const [fileCheckHours, setFileCheckHours] = useState(String(settings.libraryFileCheckHours));
  const [retentionDays, setRetentionDays] = useState(String(recycleBin.retentionDays));
  const [busy, setBusy] = useState(false);

  async function saveFileCheck(value: string) {
    setFileCheckHours(value);
    const hours = Number(value);
    // Same 1..168 the server clamps to. Saving a number Deluno will not run at
    // and then showing it back would be worse than refusing it.
    if (!Number.isFinite(hours) || hours < 1 || hours > 168) return;

    setBusy(true);
    try {
      const response = await authedFetch("/api/settings", {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ libraryFileCheckHours: hours })
      });
      if (!response.ok) throw new Error("save-failed");

      toast.success(
        hours === 1
          ? "Deluno will check your files every hour."
          : `Deluno will check your files every ${hours} hours.`
      );
      onChanged();
    } catch {
      toast.error("That schedule could not be saved.");
    } finally {
      setBusy(false);
    }
  }

  async function saveRetention(value: string) {
    setRetentionDays(value);
    const days = Number(value);
    if (!Number.isFinite(days) || days < 1) return;

    setBusy(true);
    try {
      const response = await authedFetch("/api/recycle-bin/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...recycleBin, retentionDays: days })
      });
      if (!response.ok) throw new Error("save-failed");

      toast.success(
        days === 1
          ? "You have one day to change your mind about a removed file."
          : `You have ${days} days to change your mind about a removed file.`
      );
      onChanged();
    } catch {
      toast.error("That retention could not be saved.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="grid gap-[var(--grid-gap)] sm:grid-cols-2">
      <Field
        label="Check my files every"
        help="How often Deluno looks at whether the files it thinks you have are still on disk. A local pool can afford hourly; a NAS that spins up to answer should not be woken every hour to be asked."
      >
        <PresetField
          inputType="number"
          value={fileCheckHours}
          onChange={(value) => void saveFileCheck(value)}
          options={[
            { value: "1", label: "Every hour" },
            { value: "6", label: "Every 6 hours" },
            { value: "24", label: "Once a day" },
            { value: "168", label: "Once a week" }
          ]}
          customLabel="Custom"
          customPlaceholder="1–168 hours"
        />
      </Field>

      <Field
        label="Keep removed files for"
        help="A file you remove is recycled rather than deleted, and this is how long you have to change your mind. After that Deluno clears it on its own — or you can empty the bin yourself, and it will say exactly what that takes."
      >
        <PresetField
          inputType="number"
          value={retentionDays}
          onChange={(value) => void saveRetention(value)}
          options={[
            { value: "7", label: "A week" },
            { value: "14", label: "A fortnight" },
            { value: "30", label: "A month" },
            { value: "90", label: "Three months" }
          ]}
          customLabel="Custom"
          customPlaceholder="Days"
        />
      </Field>

      <p aria-live="polite" className="sr-only">
        {busy ? "Saving" : "Saved"}
      </p>
    </div>
  );
}
