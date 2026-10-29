import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { DiscoverFilterBar } from "./discover-filter-bar";
import { LANGUAGES, NO_FILTERS, type DiscoverFilters } from "../../lib/discover-filters";

function bar(kind: "movies" | "series" = "movies", filters: DiscoverFilters = NO_FILTERS) {
  const onChange = vi.fn();
  render(<DiscoverFilterBar kind={kind} filters={filters} onChange={onChange} />);
  return onChange;
}

const optionsOf = (name: string) => within(screen.getByRole("combobox", { name })).getAllByRole("option").map((option) => [option.getAttribute("value"), option.textContent]);

describe("Discover's filters (#1429)", () => {
  it("offers each choice in plain words", () => {
    bar();

    expect(optionsOf("Release")).toEqual([["", "Released or upcoming"], ["released", "Already released"], ["upcoming", "Not out yet"]]);
    expect(optionsOf("Lowest rating")).toEqual([["", "Any rating"], ["5", "5 and up"], ["6", "6 and up"], ["7", "7 and up"], ["8", "8 and up"]]);
    expect(optionsOf("Order")).toEqual([["", "Most popular first"], ["rating", "Highest rated first"], ["newest", "Newest first"], ["oldest", "Oldest first"]]);
    expect(optionsOf("Original language")).toEqual([["", "Any language"], ...LANGUAGES.map((language) => [language.value, language.label])]);
    expect(LANGUAGES.map((language) => language.label)).toEqual([
      "English", "French", "German", "Spanish", "Italian", "Portuguese", "Japanese", "Korean", "Chinese", "Hindi", "Russian", "Swedish", "Danish", "Dutch"
    ]);
  });

  it("hands back the choice with the rest unchanged", () => {
    const onChange = bar("movies", { ...NO_FILTERS, ratingMin: "6" });

    fireEvent.change(screen.getByRole("combobox", { name: "Release" }), { target: { value: "released" } });
    expect(onChange).toHaveBeenLastCalledWith({ ...NO_FILTERS, ratingMin: "6", release: "released" });
    fireEvent.change(screen.getByRole("combobox", { name: "Order" }), { target: { value: "newest" } });
    expect(onChange).toHaveBeenLastCalledWith({ ...NO_FILTERS, ratingMin: "6", sort: "newest" });
    fireEvent.change(screen.getByRole("combobox", { name: "Original language" }), { target: { value: "ko" } });
    expect(onChange).toHaveBeenLastCalledWith({ ...NO_FILTERS, ratingMin: "6", language: "ko" });
  });

  it("keeps only digits, four at most, in either year", () => {
    const onChange = bar();

    fireEvent.change(screen.getByRole("textbox", { name: "From year" }), { target: { value: "19a9-9x1" } });
    expect(onChange).toHaveBeenLastCalledWith({ ...NO_FILTERS, yearFrom: "1999" });
    fireEvent.change(screen.getByRole("textbox", { name: "To year" }), { target: { value: "20x30" } });
    expect(onChange).toHaveBeenLastCalledWith({ ...NO_FILTERS, yearTo: "2030" });
    fireEvent.change(screen.getByRole("textbox", { name: "To year" }), { target: { value: "203012" } });
    expect(onChange).toHaveBeenLastCalledWith({ ...NO_FILTERS, yearTo: "2030" });
  });

  it("toggles a genre on and off, and marks the genres that are on", () => {
    const onChange = bar("movies", { ...NO_FILTERS, genres: [28] });

    const action = screen.getByRole("button", { name: "Action" });
    const comedy = screen.getByRole("button", { name: "Comedy" });
    expect(action).toHaveAttribute("aria-pressed", "true");
    expect(action.className).toContain("text-primary");
    expect(comedy).toHaveAttribute("aria-pressed", "false");
    expect(comedy.className).not.toContain("text-primary");
    expect(comedy.className).toContain("text-muted-foreground");
    fireEvent.click(comedy);
    expect(onChange).toHaveBeenLastCalledWith({ ...NO_FILTERS, genres: [28, 35] });
    fireEvent.click(action);
    expect(onChange).toHaveBeenLastCalledWith({ ...NO_FILTERS, genres: [] });
  });

  it("names the genres for films or for shows, and clears everything with one button", () => {
    const onChange = bar("series", { ...NO_FILTERS, genres: [18] });

    expect(screen.getByRole("group", { name: "Genres of shows" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Kids" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Horror" })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Clear filters" }));
    expect(onChange).toHaveBeenCalledWith(NO_FILTERS);
  });

  it("calls the film genres films", () => {
    bar("movies");

    expect(screen.getByRole("group", { name: "Genres of films" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Horror" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Kids" })).toBeNull();
  });
});
