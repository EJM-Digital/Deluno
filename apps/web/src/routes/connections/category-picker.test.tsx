import { fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { CategoryPicker } from "./category-picker";
import type { MediaScope } from "./presets";

function Host({ initial, scope }: { initial: string; scope: MediaScope }) {
  const [value, setValue] = useState(initial);
  return (
    <>
      <CategoryPicker value={value} onChange={setValue} scope={scope} />
      <output data-testid="value">{value}</output>
    </>
  );
}
const value = () => screen.getByTestId("value").textContent;

describe("the category picker", () => {
  it("ticks and clears a category, keeping the comma-separated value", () => {
    render(<Host initial="2040" scope="both" />);
    expect((screen.getByRole("checkbox", { name: /^HD ?2040$/ }) as HTMLInputElement).checked).toBe(true);
    fireEvent.click(screen.getByRole("checkbox", { name: /^UHD ?2045$/ }));
    expect(value()).toBe("2040,2045");
    fireEvent.click(screen.getByRole("checkbox", { name: /^HD ?2040$/ }));
    expect(value()).toBe("2045");
  });

  it("shows only the group the scope uses: films for movies, series for TV, both for both", () => {
    const { unmount } = render(<Host initial="" scope="movies" />);
    expect(screen.getByText("Movies", { selector: "p" })).toBeTruthy();
    expect(screen.queryByText("TV", { selector: "p" })).toBeNull();
    unmount();
    const tv = render(<Host initial="" scope="tv" />);
    expect(screen.queryByText("Movies", { selector: "p" })).toBeNull();
    expect(screen.getByText("TV", { selector: "p" })).toBeTruthy();
    tv.unmount();
    render(<Host initial="" scope="both" />);
    expect(screen.getByText("Movies", { selector: "p" })).toBeTruthy();
    expect(screen.getByText("TV", { selector: "p" })).toBeTruthy();
  });

  it("lists a number the indexer reports as Custom, says which family it is near, and can clear it", () => {
    render(<Host initial="2040,2099,7000,abc" scope="both" />);
    expect(screen.getByText("Custom")).toBeTruthy();
    expect(screen.getByText("(custom, near Movies 2000)")).toBeTruthy();
    expect(screen.getAllByText("(custom)")).toHaveLength(2);
    fireEvent.click(screen.getByRole("checkbox", { name: /2099/ }));
    expect(value()).toBe("2040,7000,abc");
  });

  it("shows no Custom group when every number is a standard one", () => {
    render(<Host initial="2040" scope="both" />);
    expect(screen.queryByText("Custom")).toBeNull();
  });
});
