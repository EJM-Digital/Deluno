import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { Drawer } from "./drawer";

describe("drawer title (#1168)", () => {
  it("wraps to two lines and carries the whole title as a tooltip", () => {
    const title = "Download failed · The Twilight Zone S01E02 One for the Angels 1080p AMZN WEB-DL DDP2.0 H.264-GROUP";
    render(
      <Drawer open onOpenChange={() => undefined} title={title}>
        <p>body</p>
      </Drawer>
    );
    const heading = screen.getByRole("dialog").querySelector("[title]") as HTMLElement;
    expect(heading.getAttribute("title")).toBe(title);
    expect(heading.className).toContain("line-clamp-2");
    expect(heading.className).not.toMatch(/(^|\s)truncate(\s|$)/);
  });
});
