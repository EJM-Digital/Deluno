import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { boldTitle } from "./stream";

describe("boldTitle", () => {
  it("puts the title in bold where the sentence says it", () => {
    const { container } = render(<p>{boldTitle("Imported Sintel (2010) into your library", "Sintel (2010)")}</p>);
    expect(container.querySelector("b")?.textContent).toBe("Sintel (2010)");
    expect(container.textContent).toBe("Imported Sintel (2010) into your library");
  });
  it("leaves a sentence that does not say the title alone", () => {
    const { container } = render(<p>{boldTitle("Searched TV", "Sintel")}</p>);
    expect(container.querySelector("b")).toBeNull();
  });
});
