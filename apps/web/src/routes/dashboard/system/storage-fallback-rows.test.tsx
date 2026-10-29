import { describe, expect, it } from "vitest";
import type { DashboardSummary } from "../../../lib/api";
import { drivesOf } from "./storage-card";

const summary = {
  storage: {
    volumes: [
      { id: "v1", label: "Media", totalBytes: 100, freeBytes: 40, paths: ["/media"] },
      { id: "v2", label: "Unread", totalBytes: null, freeBytes: null, paths: [] }
    ]
  }
} as unknown as DashboardSummary;

describe("System: storage fallback", () => {
  it("falls back to the summary's volumes with free space only, and leaves out a volume it cannot read", () => {
    const drives = drivesOf(undefined, summary);
    expect(drives).toHaveLength(1);
    expect(drives[0]).toMatchObject({ key: "v1", name: "Media", path: "/media", total: 100, free: 40, mine: 0, fullInDays: null, io: null, health: "unknown" });
  });

  it("uses the overview's drives instead when there is one", () => {
    const overview = [{ name: "Disk", path: "D:", totalBytes: 500, freeBytes: 200, delunoBytes: 10, fullInDays: 30, health: "ok", healthNote: null, io: null }] as never;
    const drives = drivesOf(overview, summary);
    expect(drives.map((drive) => drive.name)).toEqual(["Disk"]);
  });
});
