import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { TmdbAndTvdbAttribution, TmdbAttribution } from "./tmdb-attribution";

describe("TmdbAttribution", () => {
  it("full form: TMDb's required sentence word for word, and the logo links to themoviedb.org", () => {
    render(<TmdbAttribution full />);
    expect(screen.getByText("This product uses the TMDB API but is not endorsed or certified by TMDB.")).toBeInTheDocument();
    const link = screen.getByRole("link", { name: "TMDb" });
    expect(link).toHaveAttribute("href", "https://www.themoviedb.org");
    expect(link).toHaveAttribute("target", "_blank");
    expect(link).toHaveAttribute("rel", "noreferrer noopener");
  });

  it("short form: says where film and TV details come from and links TMDB", () => {
    render(<TmdbAttribution />);
    expect(screen.getByText(/Film and TV details from/)).toBeInTheDocument();
    const link = screen.getByRole("link", { name: "TMDB" });
    expect(link).toHaveAttribute("href", "https://www.themoviedb.org");
    expect(link).toHaveAttribute("rel", "noreferrer noopener");
  });
  it("credits both sources in one line on a show's page, each linked to its site", () => {
    render(<TmdbAndTvdbAttribution />);
    expect(screen.getByText(/Details from/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "TMDB" })).toHaveAttribute("href", "https://www.themoviedb.org");
    expect(screen.getByRole("link", { name: "TheTVDB" })).toHaveAttribute("href", "https://thetvdb.com");
  });
});
