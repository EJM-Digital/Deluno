import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { SetupProgressLadder } from "./setup-progress-ladder";
import type { SetupStatusModel, SetupStatusStep } from "../../lib/setup-status";

function step(overrides: Partial<SetupStatusStep> & { id: string; number: number; title: string }): SetupStatusStep {
  return {
    status: `${overrides.title} status`, action: `Open ${overrides.title}`, to: `/settings/${overrides.id}`,
    complete: false, optional: false, state: "not-started", ...overrides
  } as SetupStatusStep;
}

function ladder(steps: SetupStatusStep[]) {
  const model = { steps, completedCount: steps.filter((s) => s.complete).length, totalCount: steps.length, isComplete: false, readiness: "not-ready", summary: "Start with the next step.", attentionItems: [] } as unknown as SetupStatusModel;
  return render(<MemoryRouter><SetupProgressLadder status={model} /></MemoryRouter>);
}

const tile = (name: RegExp) => screen.getByRole("link", { name });

describe("the setup ladder tiles, low rows (#1329)", () => {
  it("a complete, a failed, the current and a not-started step each read their own state with their own icon, colour and action label", () => {
    const { container } = ladder([
      step({ id: "library", number: 1, title: "Media Management", state: "complete", complete: true, status: "1 movie library" }),
      step({ id: "media-plans", number: 2, title: "Library Profiles", state: "not-started", status: "No quality decision" }),
      step({ id: "connections", number: 3, title: "Find & Download", state: "failed", status: "Nothing connected" }),
      step({ id: "automation", number: 4, title: "Automation", state: "not-started", status: "Paused" })
    ]);

    const complete = tile(/^1\. Media Management: 1 movie library$/);
    expect(complete.className).toContain("border-success");
    expect(complete.querySelector("svg")!.getAttribute("class")).toMatch(/circle-check/);
    expect(complete.textContent).toContain("Open Media Management");

    const failed = tile(/^3. Find & Download: Needs attention · Nothing connected$/);
    expect(failed.className).toContain("border-destructive");
    expect(failed.querySelector("svg")!.getAttribute("class")).toMatch(/circle-x/);
    expect(failed.textContent).toContain("Needs attention · Nothing connected");
    expect(failed.textContent).toContain("Open Find & Download");

    const current = tile(/^2. Library Profiles: No quality decision$/);
    expect(current.className).toMatch(/(^| )border-primary\/35/);
    expect(current.querySelector("svg")!.getAttribute("class")).not.toMatch(/circle-check|circle-x/);

    const later = tile(/^4\. Automation: Paused$/);
    expect(later.className).not.toMatch(/(^| )border-primary\/35/);
    expect(later.className).not.toContain("border-success");
    expect(later.className).not.toContain("border-destructive");
    expect(container.querySelectorAll("ol > li")).toHaveLength(4);
  });

  it("an optional step is left off the ladder", () => {
    ladder([
      step({ id: "library", number: 1, title: "Media Management", state: "complete", complete: true }),
      step({ id: "discovery", number: 5, title: "Discover Media", optional: true })
    ]);

    expect(screen.queryByRole("link", { name: /Discover Media/ })).toBeNull();
    expect(screen.getAllByRole("link")).toHaveLength(1);
  });

  it("only the connector that leads from a finished step to the current one pulses", () => {
    const { container } = ladder([
      step({ id: "library", number: 1, title: "Media Management", state: "complete", complete: true }),
      step({ id: "media-plans", number: 2, title: "Library Profiles", state: "complete", complete: true }),
      step({ id: "connections", number: 3, title: "Find & Download" }),
      step({ id: "automation", number: 4, title: "Automation" })
    ]);

    const connectors = Array.from(container.querySelectorAll('li > span[aria-hidden="true"]'));
    const pulsing = connectors.map((connector) => (connector.querySelector("svg")!.getAttribute("class") ?? "").includes("animate-pulse"));
    expect(pulsing).toEqual([false, true, false]);
  });
});
