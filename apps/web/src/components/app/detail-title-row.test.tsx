import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { DetailTitleRow } from "./detail-title-row";

describe("DetailTitleRow (#1441)", () => {
  it("without a logo the heading is the title text", () => {
    render(<DetailTitleRow eyebrow="Movie" title="Oppenheimer" logoUrl={null} shield={<button>Shield</button>} />);
    expect(screen.getByRole("heading", { level: 1, name: "Oppenheimer" })).toHaveTextContent("Oppenheimer");
    expect(screen.getByRole("button", { name: "Shield" })).toBeInTheDocument();
  });

  it("with a logo the logo is the heading, named for the title, with no second copy of the title", () => {
    render(<DetailTitleRow eyebrow="TV series" title="Chernobyl" logoUrl="https://fixture.invalid/logo.png" shield={null} />);
    const heading = screen.getByRole("heading", { level: 1, name: "Chernobyl" });
    expect(heading).toHaveAttribute("title", "Chernobyl");
    expect(heading.querySelector("img")).toHaveAttribute("alt", "Chernobyl");
    expect(heading.textContent?.trim()).toBe("");
  });
});
