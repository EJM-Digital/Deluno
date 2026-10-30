import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { ProfileSizeRule, QualityTierDefinition } from "../../lib/api";
import { ProfileSizeSteps, describe as describeSize } from "./profile-size-steps";

const tier = (name: string, over: Partial<QualityTierDefinition> = {}): QualityTierDefinition => ({
  name, rank: 1, movieMinGb: 2, moviePreferredGb: 5, movieMaxGb: 10, episodeMinMb: 200, episodePreferredMb: 800, episodeMaxMb: 2000, scoreCeiling: 100, ...over
});
const rule = (quality: string, over: Partial<ProfileSizeRule> = {}): ProfileSizeRule => ({ quality, minGb: 3, maxGb: 8, minMb: 300, maxMb: 1500, ...over });

describe("a profile's size answers", () => {
  it("tells the person to answer the first question while no tier is chosen", () => {
    render(<ProfileSizeSteps mediaType="movies" allowed={[]} rules={[]} tiers={[]} onChange={vi.fn()} />);
    expect(screen.getByText(/Answer the first question/)).toBeTruthy();
  });

  it("lists the best tier first, starting each handle where files of that tier land", () => {
    render(<ProfileSizeSteps mediaType="movies" allowed={["WEB 720p", "WEB 1080p"]} rules={[]} tiers={[tier("web 1080p")]} onChange={vi.fn()} />);
    const names = screen.getAllByRole("listitem").map((li) => li.querySelector("span")!.textContent);
    expect(names).toEqual(["WEB 1080p", "WEB 720p"]);
    expect(screen.getByText("2.0 GB–10.0 GB")).toBeTruthy();
    expect(screen.getByText("0.1 GB–130.0 GB")).toBeTruthy();
    expect(screen.getByText("Accepts the whole range WEB 1080p films normally land in.")).toBeTruthy();
  });

  it("shows the profile's own stored answer in place of the usual range, found whatever the capitals", () => {
    render(<ProfileSizeSteps mediaType="movies" allowed={["WEB 1080p"]} rules={[rule("web 1080p", { minGb: 4, maxGb: 0 })]} tiers={[tier("WEB 1080p")]} onChange={vi.fn()} />);
    expect(screen.getByText("4.0 GB–no limit")).toBeTruthy();
  });

  it("moving a film handle replaces that tier's rule and keeps the others", () => {
    const onChange = vi.fn();
    render(<ProfileSizeSteps mediaType="movies" allowed={["WEB 720p", "WEB 1080p"]} rules={[rule("WEB 720p"), rule("web 1080p")]} tiers={[]} onChange={onChange} />);
    fireEvent.keyDown(screen.getByLabelText("WEB 1080p largest file this profile accepts"), { key: "ArrowRight" });
    expect(onChange).toHaveBeenCalledWith([rule("WEB 720p"), rule("web 1080p", { maxGb: 8.1 })]);
  });

  it("an untouched tier becomes the profile's own once a handle moves", () => {
    const onChange = vi.fn();
    render(<ProfileSizeSteps mediaType="movies" allowed={["WEB 1080p"]} rules={[]} tiers={[tier("WEB 1080p")]} onChange={onChange} />);
    fireEvent.keyDown(screen.getByLabelText("WEB 1080p smallest file this profile accepts"), { key: "ArrowRight" });
    expect(onChange).toHaveBeenCalledWith([{ quality: "WEB 1080p", minGb: 2.1, maxGb: 10, minMb: 200, maxMb: 2000 }]);
  });

  it("writes episodes in megabytes and moves them in steps of 50", () => {
    const onChange = vi.fn();
    render(<ProfileSizeSteps mediaType="tv" allowed={["WEB 1080p"]} rules={[rule("WEB 1080p")]} tiers={[tier("WEB 1080p")]} onChange={onChange} />);
    expect(screen.getByText("300 MB–1500 MB")).toBeTruthy();
    expect(screen.getByText(/WEB 1080p episodes outside 300–1500 MB are refused|Narrower than usual/)).toBeTruthy();
    fireEvent.keyDown(screen.getByLabelText("WEB 1080p smallest file this profile accepts"), { key: "ArrowRight" });
    expect(onChange).toHaveBeenCalledWith([rule("WEB 1080p", { minMb: 350 })]);
  });
});

describe("what a size answer means, in words", () => {
  it("says any size is accepted when both ends are zero", () => {
    expect(describeSize("WEB 1080p", 0, 0, true, 2, 10)).toBe("Any size is accepted for WEB 1080p.");
  });

  it("says it takes the whole usual range, with or without an upper limit", () => {
    expect(describeSize("Q", 1, 12, true, 2, 10)).toBe("Accepts the whole range Q films normally land in.");
    expect(describeSize("Q", 1, 0, false, 2, 10)).toBe("Accepts the whole range Q episodes normally land in.");
  });

  it("says narrower than usual when both ends are tighter", () => {
    expect(describeSize("Q", 4, 6, true, 2, 10)).toBe("Narrower than usual — Q films outside 4–6 GB are refused.");
  });

  it("says what a tighter top or a tighter bottom rules out", () => {
    expect(describeSize("Q", 1, 6, true, 2, 10)).toBe("Refuses Q films over 6 GB, which the largest normally exceed.");
    expect(describeSize("Q", 4, 0, false, 2, 10)).toBe("Refuses Q episodes under 4 MB, which rules out the smallest encodes.");
    expect(describeSize("Q", 4, 12, true, 2, 10)).toBe("Refuses Q films under 4 GB, which rules out the smallest encodes.");
  });

});
