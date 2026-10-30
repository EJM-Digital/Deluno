import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { DEFAULT_DISPLAY_PREFERENCES } from "../../lib/display-preferences";
import { CountryReleaseRow } from "./country-release-row";

const facts = (over = {}) => ({ country: "US", inCinemas: null, digital: null, physical: null, certification: null, ...over });
const prefs = { ...DEFAULT_DISPLAY_PREFERENCES, shortDateFormat: "iso" as const, timezone: "UTC" };

describe("a country's release dates and certification", () => {
  it("lists cinema, digital and physical dates and the certification", () => {
    render(<CountryReleaseRow facts={facts({ certification: "PG", inCinemas: "2024-03-01T12:00:00Z", digital: "2024-05-02T12:00:00Z", physical: "2024-06-03T12:00:00Z" })} preferences={prefs} />);
    expect(screen.getByText("US")).toBeTruthy();
    expect(screen.getByText("PG")).toBeTruthy();
    expect(screen.getByText("Cinemas 2024-03-01 · Digital 2024-05-02 · Physical 2024-06-03")).toBeTruthy();
  });

  it("says so when there is no date, and shows no certification chip", () => {
    render(<CountryReleaseRow facts={facts()} preferences={prefs} highlighted />);
    expect(screen.getByText("No release date on file")).toBeTruthy();
    expect(screen.queryByText("PG")).toBeNull();
  });
});
