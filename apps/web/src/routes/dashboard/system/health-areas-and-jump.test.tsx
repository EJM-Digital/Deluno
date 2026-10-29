import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { serverRow, summaryOf } from "../../../lib/attention-summary-fixtures";
import { emptySummary } from "../summary-fixtures";
import { HealthCard } from "./health-card";
import { buildHealth } from "./problems";

// #1329 (dashboard, System tab): the area chips on the Health card wear the colour of the area's worst problem, and "N need you" on
// This instance makes the Health card scroll into view and ring.

afterEach(cleanup);

const check = (id: string, category: string, status: string) =>
  ({ id, category, name: `${id} check`, status, message: `${id} message`, cause: null, fix: null, detailsJson: null, lastRunUtc: "2026-10-01T09:00:00Z", problemSinceUtc: status === "problem" ? "2026-10-01T08:00:00Z" : null, snoozedUntilUtc: null }) as never;

describe("System: Health areas", () => {
  it("colours each area's dot by its worst problem: red for a failing check, amber for a warning from the server, green for none", () => {
    const model = buildHealth({
      summary: emptySummary(),
      attention: summaryOf([serverRow({ id: "stalled-downloads", kind: "stalled", tone: "warn" })]),
      checks: [check("machine:sleep", "machine", "problem"), check("library:root", "library", "ok")]
    });
    const tone = (key: string) => model.tallies.find((tally) => tally.key === key)!.tone;
    expect(tone("machine")).toBe("bad");
    expect(tone("downloads")).toBe("warn");
    expect(tone("library")).toBe("ok");

    render(
      <MemoryRouter>
        <HealthCard model={model} hasChecks jump={0} now={Date.now()} />
      </MemoryRouter>
    );
    const dotOf = (name: RegExp) => screen.getByRole("radio", { name }).querySelector("span[aria-hidden='true']") as HTMLElement;
    expect(dotOf(/Machine/i).style.background).toContain("--sy-danger");
    expect(dotOf(/Downloads/i).style.background).toContain("--sy-warning");
    expect(dotOf(/Library/i).style.background).toContain("--sy-success");
  });

  it("scrolls the Health card into view and rings it when 'N need you' is pressed, and rings it again on the next press", async () => {
    const scrolled = vi.fn();
    const original = window.HTMLElement.prototype.scrollIntoView;
    window.HTMLElement.prototype.scrollIntoView = scrolled;
    try {
      const model = buildHealth({ summary: emptySummary(), attention: summaryOf([]), checks: [check("machine:sleep", "machine", "problem")] });
      const view = render(
        <MemoryRouter>
          <HealthCard model={model} hasChecks jump={0} now={Date.now()} />
        </MemoryRouter>
      );
      expect(scrolled).not.toHaveBeenCalled();
      expect(screen.getByRole("region", { name: "Health" }).className).not.toContain("sy-flash");

      view.rerender(
        <MemoryRouter>
          <HealthCard model={model} hasChecks jump={1} now={Date.now()} />
        </MemoryRouter>
      );

      expect(scrolled).toHaveBeenCalledTimes(1);
      await waitFor(() => expect(screen.getByRole("region", { name: "Health" }).className).toContain("sy-flash"));
      await waitFor(() => expect(screen.getByRole("region", { name: "Health" }).className).not.toContain("sy-flash"), { timeout: 3000 });

      fireEvent.click(screen.getByRole("radio", { name: /Machine/i }));
      view.rerender(
        <MemoryRouter>
          <HealthCard model={model} hasChecks jump={2} now={Date.now()} />
        </MemoryRouter>
      );
      expect(scrolled.mock.calls.length).toBeGreaterThan(1);
      // Pressing it also clears the area filter, so the problems are what show.
      expect(screen.getByRole("radio", { name: /Machine/i })).toHaveAttribute("aria-checked", "false");
    } finally {
      window.HTMLElement.prototype.scrollIntoView = original;
    }
  });
});
