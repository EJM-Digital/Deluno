import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { TvdbAttribution } from "./tvdb-attribution";

describe("TvdbAttribution", () => {
  it("links to TheTVDB, as its API terms require", () => {
    render(<TvdbAttribution />);
    const link = screen.getByRole("link", { name: "TheTVDB" });
    expect(link).toHaveAttribute("href", "https://thetvdb.com");
    expect(screen.getByText(/TV metadata provided by/)).toBeInTheDocument();
  });
});
