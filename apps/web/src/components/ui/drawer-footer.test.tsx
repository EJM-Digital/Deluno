import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { DrawerFooter } from "./drawer";

// #1151: a freshly opened drawer showed "Saved just now" or "Routing saved" from an earlier drawer.
describe("DrawerFooter", () => {
  it("does not show a leftover save message on a drawer that has not saved anything", () => {
    render(<DrawerFooter state="clean" message="Routing saved" saveLabel="Save" onCancel={() => undefined} />);

    expect(screen.queryByText("Routing saved")).not.toBeInTheDocument();
  });

  it("shows the message once this drawer has saved", () => {
    render(<DrawerFooter state="saved" message="Routing saved" saveLabel="Save" onCancel={() => undefined} />);

    expect(screen.getByText("Routing saved")).toBeVisible();
  });

  it("still shows a standing note on a drawer that only explains", () => {
    render(<DrawerFooter state="clean" note="Wanted because it is missing." readOnly saveLabel="Close" onCancel={() => undefined} />);

    expect(screen.getByText("Wanted because it is missing.")).toBeVisible();
  });
});
