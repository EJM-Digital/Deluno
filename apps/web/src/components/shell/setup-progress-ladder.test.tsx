import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { SetupProgressLadder } from "./setup-progress-ladder";
import type { SetupStatusModel, SetupStatusStep } from "../../lib/setup-status";

function step(overrides: Partial<SetupStatusStep> = {}): SetupStatusStep {
  return {
    id: "library",
    number: 1,
    title: "Media Management",
    status: "1 movie library",
    action: "Review media management",
    to: "/settings/media-management",
    complete: true,
    optional: false,
    state: "complete",
    ...overrides
  } as SetupStatusStep;
}

function status(overrides: Partial<SetupStatusModel> = {}): SetupStatusModel {
  return {
    steps: [step()],
    completedCount: 5,
    totalCount: 5,
    isComplete: true,
    readiness: "ready",
    summary: "Operational setup complete.",
    attentionItems: [],
    ...overrides
  } as SetupStatusModel;
}

function renderLadder(model: SetupStatusModel) {
  return render(
    <MemoryRouter>
      <SetupProgressLadder status={model} />
    </MemoryRouter>
  );
}

describe("setup progress ladder", () => {
  it("disappears once every required step is green", () => {
    const { container } = renderLadder(status());

    // Not collapsed, not a summary line — gone. A ladder reporting "you are
    // done" is a receipt, and it was pushing the live dashboard below the fold.
    expect(container).toBeEmptyDOMElement();
    expect(screen.queryByRole("region", { name: "Setup progress" })).toBeNull();
  });

  it("stays put while a required step is outstanding", () => {
    renderLadder(
      status({
        isComplete: false,
        completedCount: 2,
        summary: "Start with step 2: Library Profiles.",
        steps: [
          step(),
          step({ id: "media-plans", number: 2, title: "Library Profiles", complete: false, state: "not-started", status: "No quality decision yet" })
        ]
      })
    );

    expect(screen.getByRole("region", { name: "Setup progress" })).toBeVisible();
    expect(screen.getByText("Complete your Deluno setup")).toBeVisible();
    expect(screen.getByText("2/5 required steps complete")).toBeVisible();
  });
});
