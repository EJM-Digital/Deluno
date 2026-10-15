import { configure, fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { DecisionExplanationItem } from "../../lib/api";
import { DecisionExplanationList } from "./decision-explanation-list";

configure({ asyncUtilTimeout: 8000 });
vi.setConfig({ testTimeout: 30_000 });

/**
 * The decision trail on a title's page (coverage register, areas movie-detail and search-grab): the latest twelve decisions with the
 * reason, the outcome when it says something else, the scope, the time and a status chip; a row opens what Deluno weighed and what
 * else it considered.
 */
const decision = (index: number, over: Partial<DecisionExplanationItem> = {}): DecisionExplanationItem => ({
  id: `d${index}`,
  occurredUtc: new Date(Date.UTC(2026, 9, 1, 12) - index * 60_000).toISOString(),
  scope: "search.movie",
  scopeLabel: "Search for Big Buck Bunny",
  status: "completed",
  reason: `Picked release ${index}`,
  inputs: {},
  outcome: `Sent release ${index}`,
  alternatives: [],
  relatedJobId: null,
  relatedEntityType: "movie",
  relatedEntityId: "m1",
  ...over
});

describe("the decision trail card", () => {
  it("says nothing has been recorded yet when there are no decisions", () => {
    render(<DecisionExplanationList decisions={[]} />);

    expect(screen.getByText("No decisions recorded yet")).toBeInTheDocument();
  });

  it("lists the latest 12 of the decisions and says how many there are", () => {
    render(<DecisionExplanationList decisions={Array.from({ length: 15 }, (_, index) => decision(index))} />);

    expect(screen.getByText("Latest 12 of 15")).toBeInTheDocument();
    expect(screen.getByText("Picked release 11")).toBeInTheDocument();
    expect(screen.queryByText("Picked release 12")).toBeNull();
  });

  it("shows the reason, the scope and the outcome under the reason only when it says something else", () => {
    render(<DecisionExplanationList decisions={[decision(1), decision(2, { reason: "Same words", outcome: "Same words" })]} />);

    expect(screen.getByText("Sent release 1")).toBeInTheDocument();
    expect(screen.getAllByText("Search for Big Buck Bunny")).toHaveLength(2);
    expect(screen.getAllByText("Same words")).toHaveLength(1);
  });

  it("a row opens a drawer with the outcome, what it weighed and the alternatives it considered", () => {
    render(
      <DecisionExplanationList
        decisions={[decision(1, {
          inputs: { Quality: "WEB 1080p", Empty: null },
          alternatives: [{ name: "Other.Release", status: "rejected", score: null, reason: "Too small." }, { name: "Older.Release", status: "held", score: 40, reason: "Held." }]
        })]}
      />
    );

    fireEvent.click(screen.getByText("Picked release 1"));

    const drawer = screen.getByRole("dialog", { name: "Picked release 1" });
    expect(within(drawer).getByText("Outcome")).toBeInTheDocument();
    expect(within(drawer).getByText("What it weighed")).toBeInTheDocument();
    expect(within(drawer).getByText("WEB 1080p")).toBeInTheDocument();
    expect(within(drawer).queryByText("Empty")).toBeNull();
    expect(within(drawer).getByText("2 considered")).toBeInTheDocument();
    expect(within(drawer).getByText("rejected")).toBeInTheDocument();
    expect(within(drawer).getByText("held · legacy score 40")).toBeInTheDocument();
  });

  it("a decision with no inputs and no alternatives shows only its outcome", () => {
    render(<DecisionExplanationList decisions={[decision(1)]} />);

    fireEvent.click(screen.getByText("Picked release 1"));

    const drawer = screen.getByRole("dialog", { name: "Picked release 1" });
    expect(within(drawer).queryByText("What it weighed")).toBeNull();
    expect(within(drawer).queryByText("Alternatives")).toBeNull();
  });
});
