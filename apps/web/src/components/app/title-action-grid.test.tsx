import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { Kbd } from "../ui/kbd";
import { TitleActionGrid } from "./title-action-grid";

describe("small layout pieces", () => {
  it("lays title actions out two to a row and keeps a caller's extra class", () => {
    render(<TitleActionGrid className="extra"><button>One</button><button>Two</button></TitleActionGrid>);
    expect(screen.getByRole("button", { name: "One" }).parentElement?.className).toContain("extra");
  });

  it("lays title actions out with no extra class", () => {
    render(<TitleActionGrid><button>Solo</button></TitleActionGrid>);
    expect(screen.getByRole("button", { name: "Solo" }).parentElement?.className).toContain("grid-cols-2");
  });

  it("draws a key hint", () => {
    render(<Kbd className="k">Ctrl</Kbd>);
    expect(screen.getByText("Ctrl").tagName).toBe("KBD");
  });
});
