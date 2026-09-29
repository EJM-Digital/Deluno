import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { TrackerDefinitionSummary } from "../../lib/api";
import { filterTrackers, languageName, TrackerPicker } from "./tracker-picker";

function tracker(overrides: Partial<TrackerDefinitionSummary>): TrackerDefinitionSummary {
  return {
    id: "site",
    name: "Site",
    description: "",
    language: "en-US",
    privacy: "public",
    links: ["https://site.example/"],
    movies: true,
    tv: true,
    loginMethod: null,
    settings: [],
    mayNeedCloudflareHelp: false,
    unsupported: null,
    origin: "data-folder",
    ...overrides
  };
}

const sites = [
  tracker({ id: "uindex", name: "Uindex" }),
  tracker({ id: "aussierules", name: "Aussierul.es", privacy: "private", language: "en-AU", description: "Australian rules football" }),
  tracker({ id: "kinozal", name: "Kinozal", privacy: "semi-private", language: "ru-RU" }),
  tracker({ id: "hdcity", name: "AAA Captcha Site", privacy: "private", unsupported: "Signing in needs a captcha solved by hand." })
];

describe("filterTrackers", () => {
  it("narrows by name, type and language, and puts the ones Deluno cannot run last", () => {
    expect(filterTrackers(sites, "", "all", "").map((item) => item.id)).toEqual(["aussierules", "kinozal", "uindex", "hdcity"]);
    expect(filterTrackers(sites, "uin", "all", "").map((item) => item.id)).toEqual(["uindex"]);
    expect(filterTrackers(sites, "", "semi-private", "").map((item) => item.id)).toEqual(["kinozal"]);
    expect(filterTrackers(sites, "", "all", "en-AU").map((item) => item.id)).toEqual(["aussierules"]);
    expect(filterTrackers(sites, "football", "all", "").map((item) => item.id)).toEqual(["aussierules"]);
    expect(filterTrackers(sites, "russian", "all", "").map((item) => item.id)).toEqual(["kinozal"]);
  });

  it("names a language in words", () => {
    expect(languageName("ru-RU")).toMatch(/Russian/);
  });
});

describe("TrackerPicker", () => {
  it("lists the sites Deluno cannot run apart, each with why in plain words, and cannot pick them", () => {
    const onPick = vi.fn();
    render(<TrackerPicker trackers={sites} onPick={onPick} />);

    expect(screen.getByText("Deluno cannot use these yet")).toBeInTheDocument();
    const blocked = screen.getByRole("option", { name: /AAA Captcha Site/ });
    expect(blocked).toBeDisabled();
    expect(within(blocked).getByText(/captcha picture solved by hand/)).toBeInTheDocument();
    fireEvent.click(blocked);
    expect(onPick).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("option", { name: /Uindex/ }));
    expect(onPick).toHaveBeenCalledWith(expect.objectContaining({ id: "uindex" }));
  });

  it("searches by name and filters by type and language", () => {
    render(<TrackerPicker trackers={sites} onPick={() => undefined} />);

    fireEvent.change(screen.getByLabelText("Which kind of tracker"), { target: { value: "private" } });
    expect(screen.queryByRole("option", { name: /Uindex/ })).not.toBeInTheDocument();
    expect(screen.getByRole("option", { name: /Aussierul\.es/ })).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("Which kind of tracker"), { target: { value: "all" } });
    fireEvent.change(screen.getByLabelText("Which language"), { target: { value: "ru-RU" } });
    expect(within(screen.getByRole("listbox", { name: "Trackers" })).getAllByRole("option")).toHaveLength(1);
    expect(screen.getByRole("option", { name: /Kinozal/ })).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText("Which language"), { target: { value: "" } });
    fireEvent.change(screen.getByLabelText("Search trackers"), { target: { value: "nothing like this" } });
    expect(screen.getByText("No tracker matches these words.")).toBeInTheDocument();
  });
});
