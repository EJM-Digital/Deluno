import { render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import type { SystemOverviewDrive } from "../../../lib/api/types/system-overview";
import { StorageCard, drivesOf } from "./storage-card";

const drive = (over: Partial<SystemOverviewDrive>): SystemOverviewDrive => ({
  name: "Media", path: "D:/Media", totalBytes: 4e12, freeBytes: 2e12, delunoBytes: 1e12, fullInDays: null,
  readBytesPerSec: 5e6, writeBytesPerSec: 2e6, iops: 120, busyPercent: 7, health: "good", healthNote: null, ...over
} as SystemOverviewDrive);

function renderCard(drives: SystemOverviewDrive[]) {
  return render(<MemoryRouter><StorageCard drives={drivesOf(drives, undefined)} /></MemoryRouter>);
}

describe("Storage on the System tab", () => {
  it("shows a drive's name and path, its free space, when it will be full, and its speed and health", () => {
    renderCard([drive({ fullInDays: 12 })]);
    const card = screen.getByRole("region", { name: "Storage" });

    expect(within(card).getByText("Media")).toBeInTheDocument();
    expect(card.querySelector(".sy-drive")?.getAttribute("title")).toBe("D:/Media");
    expect(card.textContent).toMatch(/1\.8 TB free/);
    expect(card.textContent).toContain("full in ~");
    expect(card.textContent).toContain("120");
    expect(card.textContent).toContain("IOPS");
    expect(card.textContent).toContain("7% busy");
    expect(card.textContent).toContain("healthy");
  });

  it("warns a drive with under 15% free, and not one with more", () => {
    renderCard([drive({ name: "Nearly full", freeBytes: 0.4e12 }), drive({ name: "Roomy", freeBytes: 3e12 })]);

    expect(screen.getByText("Nearly full").closest(".sy-drive")?.className).toContain("sy-warn");
    expect(screen.getByText("Roomy").closest(".sy-drive")?.className).not.toContain("sy-warn");
  });

  it("says a failing drive is failing, and shows the drive's own warning note for a warning", () => {
    renderCard([
      drive({ name: "Dying", health: "failing", healthNote: null }),
      drive({ name: "Shaky", health: "warning", healthNote: "14 reallocated sectors" })
    ]);

    expect(screen.getByText("Dying").closest(".sy-drive")?.textContent).toContain("failing");
    expect(screen.getByText("Shaky").closest(".sy-drive")?.textContent).toContain("14 reallocated sectors");
  });

  it("leaves out the speed line for a drive that has no io reading, and invents no number", () => {
    const { container } = renderCard([drive({ readBytesPerSec: null, writeBytesPerSec: null, iops: null, busyPercent: null, health: "unknown" })]);

    expect(container.textContent).not.toContain("IOPS");
    expect(container.textContent).not.toMatch(/undefined|NaN/);
  });

  it("says no drive has been read yet when there are none", () => {
    renderCard([]);

    expect(screen.getByText(/No drive has been read yet/)).toBeInTheDocument();
  });
});
