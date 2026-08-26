import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it } from "vitest";
import { HowThisWorks } from "./how-this-works";

const steps = [
  { title: "Make a library", body: "Movies, TV shows, or one of your own." },
  { title: "Name the file", body: "Naming rules turn a release name into a tidy filename." }
];

function panel(id: string) {
  return <HowThisWorks id={id} lead="A library is the thing everything else hangs off." steps={steps} />;
}

describe("How this works", () => {
  beforeEach(() => {
    window.localStorage.clear();
  });

  it("starts collapsed, so an explainer costs a click rather than a slice of every pane", () => {
    render(panel("media-management"));

    expect(screen.getByRole("button", { name: "How this works" })).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByText(/A library is the thing/)).not.toBeVisible();
  });

  it("remembers each area separately — opening one does not open the next", async () => {
    const user = userEvent.setup();
    const first = render(panel("media-management"));
    await user.click(screen.getByRole("button", { name: "How this works" }));
    expect(screen.getByRole("button", { name: "How this works" })).toHaveAttribute("aria-expanded", "true");
    first.unmount();

    render(panel("quality-and-release"));

    expect(screen.getByRole("button", { name: "How this works" })).toHaveAttribute("aria-expanded", "false");
  });

  it("keeps the chevron and the badge out of the heading's accessible name", () => {
    render(panel("media-management"));

    // Both icons are decoration. Anything readable inside the button becomes
    // part of the heading a screen reader announces (#296).
    expect(screen.getByRole("heading", { name: "How this works" })).toBeInTheDocument();
  });
});
