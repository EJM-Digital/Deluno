import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it } from "vitest";
import { BackupsCard } from "./bottom";
import { sizeText } from "./logic";

afterEach(cleanup);

const backup = (id: string, createdUtc: string, sizeBytes: number) => ({ id, fileName: `${id}.zip`, fullPath: `/b/${id}.zip`, sizeBytes, createdUtc, reason: "scheduled" }) as never;
const settings = (extra: object = {}) => ({ enabled: true, frequency: "Daily", timeOfDay: "03:00", retentionCount: 7, backupFolder: "/backups", nextRunUtc: new Date(Date.now() + 5 * 3_600_000).toISOString(), ...extra }) as never;

describe("System: Backups card", () => {
  it("names the schedule as frequency, time, how many are kept and the folder", () => {
    render(<MemoryRouter><BackupsCard backups={[]} settings={settings()} databases={null} /></MemoryRouter>);
    expect(screen.getByTitle("daily at 3:00 am · keeping 7 · /backups")).toHaveTextContent("daily at 3:00 am · keeping 7 · /backups");
    expect(screen.getByText(/^next in /)).toBeInTheDocument();
  });

  it("says \"automatic backups are off\" when the schedule is off, and \"No backup has been made yet.\" with no backups", () => {
    render(<MemoryRouter><BackupsCard backups={[]} settings={settings({ enabled: false })} databases={null} /></MemoryRouter>);
    expect(screen.getByText("automatic backups are off")).toBeInTheDocument();
    expect(screen.getByText("No backup has been made yet.")).toBeInTheDocument();
  });

  it("lists the newest three backups, newest first, each with its size and complete", () => {
    const backups = [
      backup("old", "2026-10-20T03:00:00Z", 1_000_000),
      backup("newest", "2026-10-27T03:00:00Z", 2_000_000),
      backup("mid", "2026-10-24T03:00:00Z", 1_500_000),
      backup("oldest", "2026-10-10T03:00:00Z", 900_000)
    ];
    const { container } = render(<MemoryRouter><BackupsCard backups={backups} settings={settings()} databases={null} /></MemoryRouter>);
    const rows = Array.from(container.querySelectorAll(".sy-bk-list .sy-bk-row"));
    expect(rows).toHaveLength(3);
    expect(rows.map((row) => row.querySelectorAll(".sy-r")[0].textContent)).toEqual([sizeText(2_000_000), sizeText(1_500_000), sizeText(1_000_000)]);
    expect(rows.every((row) => row.querySelectorAll(".sy-r")[1].textContent === "complete")).toBe(true);
    expect(container.querySelector(".sy-bk-list")!.textContent).not.toContain(sizeText(900_000));
  });
});
