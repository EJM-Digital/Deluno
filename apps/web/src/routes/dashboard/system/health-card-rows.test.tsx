import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it } from "vitest";
import { HealthCard } from "./health-card";
import type { HealthModel } from "./problems";
import { areaName } from "./logic";

afterEach(cleanup);

const bad = {
  id: "missing-root",
  area: "library",
  tone: "bad",
  title: "The movie folder is missing",
  why: "Deluno cannot find it",
  atUtc: null,
  buttons: []
};

function model(rows: unknown[], tallies = [
  { key: "library", name: areaName("library"), total: 2, ok: 1, tone: "bad" },
  { key: "downloads", name: areaName("downloads"), total: 1, ok: 1, tone: "ok" }
]): HealthModel {
  return { rows, tallies, pass: 2, total: 3, need: rows.length, more: 0, lastRunUtc: null } as unknown as HealthModel;
}

function renderCard(m: HealthModel, hasChecks = true) {
  return render(
    <MemoryRouter>
      <HealthCard model={m} hasChecks={hasChecks} jump={0} now={Date.now()} />
    </MemoryRouter>
  );
}

describe("System: Health card", () => {
  it("sends \"All checks\" to the System health page, named \"Open all health checks\"", () => {
    renderCard(model([bad]));
    const link = screen.getByRole("link", { name: "Open all health checks" });
    expect(link).toHaveTextContent("All checks →");
    expect(link).toHaveAttribute("href", "/system");
  });

  it("says \"Nothing is wrong. Deluno checks everything every 15 minutes.\" when no problem is listed, and \"Waiting for the health checks.\" before any check ran", () => {
    const { unmount } = renderCard(model([]));
    expect(screen.getByText("Nothing is wrong. Deluno checks everything every 15 minutes.")).toBeInTheDocument();
    unmount();
    renderCard(model([]), false);
    expect(screen.getByText("Waiting for the health checks.")).toBeInTheDocument();
    expect(screen.queryByText(/Nothing is wrong/)).toBeNull();
  });

  it("filters to one area when its chip is pressed, says \"Nothing to show for\" an area with no problem, and goes back to the problems on a second press", () => {
    renderCard(model([bad]));
    expect(screen.getByText("The movie folder is missing")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("radio", { name: new RegExp(areaName("downloads")) }));
    expect(screen.getByText(`Nothing to show for ${areaName("downloads")}.`)).toBeInTheDocument();
    expect(screen.queryByText("The movie folder is missing")).toBeNull();

    fireEvent.click(screen.getByRole("radio", { name: new RegExp(areaName("downloads")) }));
    expect(screen.getByText("The movie folder is missing")).toBeInTheDocument();
    expect(screen.queryByText(/Nothing to show for/)).toBeNull();
  });
});
