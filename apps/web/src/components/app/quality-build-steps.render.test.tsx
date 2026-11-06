import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Chip } from "../ui/chip";
import { QualityBuildSteps, guideIntentFor, type QualityBuildStepsProps } from "./quality-build-steps";

const judge = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api")>()), judgeDraftProfile: judge }));

beforeEach(() => { vi.useFakeTimers(); judge.mockReset(); judge.mockResolvedValue({ candidateEvaluation: { status: "meetsPlan", reasons: ["All targets are met"] } }); });
afterEach(() => { cleanup(); vi.useRealTimers(); });

const format = (id: string, name: string, score: number, trashId: string | null, mediaType = "movies") => ({ id, name, mediaType, score, conditions: "", upgradeAllowed: true, trashId, createdUtc: "", updatedUtc: "" });
const guide = { customFormats: [
  { trashId: "t-hdr", category: "hdr" }, { trashId: "t-audio", category: "audio" }, { trashId: "t-bad", category: "unwanted" },
  { trashId: "t-group", category: "groups" }, { trashId: "t-lang", category: "language" }, { trashId: "t-tvhdr", category: "hdr" }, { trashId: "t-hdr2", category: "hdr" }
] } as never;
const formats = [
  format("hdr", "HDR10", 100, "t-hdr"), format("audio", "Atmos", -50, "t-audio"), format("bad", "x265 (HD)", 100, "t-bad"),
  format("group", "Group Alpha", 600, "t-group"), format("lang", "Original", 0, "t-lang"), format("tvhdr", "TV HDR", 100, "t-tvhdr", "tv"),
  format("mine1", "My rule", 200, null), format("mine2", "Second rule", -20, null), format("mine3", "Third rule", 0, null), format("tvmine", "TV mine", 0, null, "tv"), format("hdr2", "Dolby Vision", -50, "t-hdr2"), format("blank", "", 0, null)
] as never;

const props = (overrides: Partial<QualityBuildStepsProps> = {}): QualityBuildStepsProps => ({
  mediaType: "movies", name: "Mine", allowed: [], cutoff: "", customFormatIds: [], formatIntents: {}, upgradeUntilCutoff: true, upgradeUnknownItems: false,
  customFormats: formats, guide, onCustomFormatIdsChange: vi.fn(), onFormatIntentChange: vi.fn(),
  renderQualityControls: () => <p>quality controls</p>, renderSizeControls: () => <p>size controls</p>, ...overrides
});
const show = (overrides: Partial<QualityBuildStepsProps> = {}) => {
  const p = props(overrides);
  return { p, ...render(<QualityBuildSteps {...p} />) };
};
const step = (question: RegExp | string) => screen.getByRole("button", { name: question });
const settle = async () => { await act(async () => { await vi.advanceTimersByTimeAsync(400); }); };

describe("the guide's suggestion for a rule", () => {
  it("is never, avoid, off, prefer or strongly prefer by its score", () => {
    expect(guideIntentFor(-10000)).toBe("blocked");
    expect(guideIntentFor(-9999)).toBe("avoid");
    expect(guideIntentFor(-1)).toBe("avoid");
    expect(guideIntentFor(0)).toBe("neutral");
    expect(guideIntentFor(1)).toBe("prefer");
    expect(guideIntentFor(499)).toBe("prefer");
    expect(guideIntentFor(500)).toBe("strong-prefer");
  });
});

describe("the seven questions", () => {
  it("lists all seven in order, with the first open and the custom rules after them", () => {
    show();
    const rows = within(screen.getByRole("list", { name: "Build this profile" })).getAllByRole("listitem");
    expect(rows).toHaveLength(8);
    expect(step(/How good, and when to stop/)).toHaveAttribute("aria-expanded", "true");
    expect(step(/How big should a file/)).toHaveAttribute("aria-expanded", "false");
    expect(screen.getByText("quality controls")).toBeInTheDocument();
    expect(screen.getByText("The tiers you will accept, best first, and the one that ends the search.")).toBeInTheDocument();
  });

  it("opens one at a time, and closes the open one when it is clicked again", () => {
    show();
    fireEvent.click(step(/What should the picture be/));
    expect(step(/What should the picture be/)).toHaveAttribute("aria-expanded", "true");
    expect(step(/How good, and when to stop/)).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByText("quality controls")).toBeNull();
    fireEvent.click(step(/What should the picture be/));
    expect(step(/What should the picture be/)).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByText("HDR and Dolby Vision, and which video codec you prefer.")).toBeNull();
  });

  it("gives the size step its own controls and the quality step none of them", () => {
    show();
    expect(screen.queryByText("size controls")).toBeNull();
    fireEvent.click(step(/How big should a file/));
    expect(screen.getByText("size controls")).toBeInTheDocument();
    expect(screen.queryByText("quality controls")).toBeNull();
  });

  it("offers a step only the rules of its own kind and of this kind of library", () => {
    show();
    fireEvent.click(step(/What should the picture be/));
    const group = screen.getByRole("group", { name: "What should the picture be?" });
    expect(within(group).getAllByRole("checkbox").map((box) => box.textContent)).toEqual(["HDR10", "Dolby Vision"]);
    cleanup();
    show({ mediaType: "tv" });
    fireEvent.click(step(/What should the picture be/));
    expect(within(screen.getByRole("group", { name: "What should the picture be?" })).getAllByRole("checkbox").map((box) => box.textContent)).toEqual(["TV HDR"]);
  });

  it("offers no group of rules to a step that has none, and the extras and advanced fields where they go", () => {
    show({ renderStepExtras: (s) => <p>extras for {s.id}</p>, renderAdvanced: (s) => <p>advanced for {s.id}</p> });
    expect(screen.queryByRole("group")).toBeNull();
    expect(screen.getByText("extras for quality")).toBeInTheDocument();
    expect(screen.getByText("advanced for quality")).toBeInTheDocument();
  });
});

describe("what each step says it has decided", () => {
  it("says no tiers are chosen yet, and nothing about size", () => {
    show();
    expect(screen.getAllByText("No tiers chosen yet")).toHaveLength(1);
    expect(screen.getByText("Nothing chosen yet")).toBeInTheDocument();
  });

  it("lists up to three tiers best first, and where it stops", () => {
    show({ allowed: ["SD", "HD", "UHD"], cutoff: "HD" });
    expect(screen.getByText("UHD → HD → SD, stops at HD")).toBeInTheDocument();
    cleanup();
    show({ allowed: ["SD", "HD", "UHD"] });
    expect(screen.getByText("UHD → HD → SD")).toBeInTheDocument();
  });

  it("cuts a long ladder after three tiers", () => {
    show({ allowed: ["480p", "720p", "1080p", "2160p"], cutoff: "1080p" });
    expect(screen.getByText("2160p → 1080p → 720p → …, stops at 1080p")).toBeInTheDocument();
  });

  it("counts the tiers that each have a size of their own", () => {
    show({ allowed: ["HD"] });
    expect(screen.getByText("Your own size for 1 tier")).toBeInTheDocument();
    cleanup();
    show({ allowed: ["SD", "HD", "UHD"] });
    expect(screen.getByText("Your own size for each of 3 tiers")).toBeInTheDocument();
  });

  it("says no preference for a step with nothing turned on, and names the rules that are on", () => {
    show({ customFormatIds: ["hdr", "bad"], formatIntents: { hdr: "prefer", bad: "blocked" } });
    expect(screen.getByText("HDR10")).toBeInTheDocument();
    expect(screen.getByText("Never x265 (HD)")).toBeInTheDocument();
    expect(screen.getAllByText("No preference — anything is fine").length).toBeGreaterThan(0);
  });
});

describe("ticking a rule", () => {
  it("adds it, and saves the guide's suggestion as the person's own answer", () => {
    const { p } = show();
    fireEvent.click(step(/What should the sound be/));
    fireEvent.click(screen.getByRole("checkbox", { name: "Atmos" }));
    expect(p.onCustomFormatIdsChange).toHaveBeenCalledWith(["audio"]);
    expect(p.onFormatIntentChange).toHaveBeenCalledWith("audio", "avoid");
  });

  it("saves never, whatever the guide scores it, in the step that asks what they never want", () => {
    const { p } = show();
    fireEvent.click(step(/What do you never want/));
    fireEvent.click(screen.getByRole("checkbox", { name: "x265 (HD)" }));
    expect(p.onFormatIntentChange).toHaveBeenCalledWith("bad", "blocked");
    cleanup();
    const second = show();
    fireEvent.click(step(/Who do you want it from/));
    fireEvent.click(screen.getByRole("checkbox", { name: "Group Alpha" }));
    expect(second.p.onFormatIntentChange).toHaveBeenCalledWith("group", "strong-prefer");
  });

  it("keeps the answer the person already gave when they tick it again", () => {
    const { p } = show({ customFormatIds: [], formatIntents: { hdr: "avoid" } });
    fireEvent.click(step(/What should the picture be/));
    fireEvent.click(screen.getByRole("checkbox", { name: "HDR10" }));
    expect(p.onCustomFormatIdsChange).toHaveBeenCalledWith(["hdr"]);
    expect(p.onFormatIntentChange).not.toHaveBeenCalled();
  });

  it("saves the answer of the rule that was ticked, not of another in the step", () => {
    const { p } = show();
    fireEvent.click(step(/What should the picture be/));
    fireEvent.click(screen.getByRole("checkbox", { name: "Dolby Vision" }));
    expect(p.onFormatIntentChange).toHaveBeenCalledWith("hdr2", "avoid");
    fireEvent.click(screen.getByRole("checkbox", { name: "HDR10" }));
    expect(p.onFormatIntentChange).toHaveBeenLastCalledWith("hdr", "prefer");
  });

  it("removes a rule that has no answer without saving one for it", () => {
    const { p } = show({ customFormatIds: ["hdr"], formatIntents: {} });
    fireEvent.click(step(/What should the picture be/));
    fireEvent.click(screen.getByRole("checkbox", { name: "HDR10" }));
    expect(p.onCustomFormatIdsChange).toHaveBeenCalledWith([]);
    expect(p.onFormatIntentChange).not.toHaveBeenCalled();
  });

  it("removes it and changes no answer", () => {
    const { p } = show({ customFormatIds: ["hdr", "audio"], formatIntents: { hdr: "prefer" } });
    fireEvent.click(step(/What should the picture be/));
    const box = screen.getByRole("checkbox", { name: "HDR10" });
    expect(box).toHaveAttribute("aria-checked", "true");
    fireEvent.click(box);
    expect(p.onCustomFormatIdsChange).toHaveBeenCalledWith(["audio"]);
    expect(p.onFormatIntentChange).not.toHaveBeenCalled();
  });

  it("adds to the rules already chosen", () => {
    const { p } = show({ customFormatIds: ["audio"] });
    fireEvent.click(step(/What should the picture be/));
    fireEvent.click(screen.getByRole("checkbox", { name: "HDR10" }));
    expect(p.onCustomFormatIdsChange).toHaveBeenCalledWith(["audio", "hdr"]);
  });
});

describe("how much a chosen rule matters", () => {
  it("is off until the person turns it on, with the guide's suggestion one click away", () => {
    const { p } = show({ customFormatIds: ["hdr"] });
    fireEvent.click(step(/What should the picture be/));
    const radios = screen.getByRole("radiogroup", { name: "How much this profile wants HDR10" });
    expect(within(radios).getAllByRole("radio").map((radio) => [radio.textContent, radio.getAttribute("aria-checked")])).toEqual([
      ["Off", "true"], ["Never", "false"], ["Avoid", "false"], ["Prefer", "false"], ["Strongly prefer", "false"]
    ]);
    expect(within(radios).getByRole("radio", { name: "Prefer" })).toHaveAttribute("title", "Tips a close call towards releases that match.");
    fireEvent.click(screen.getByRole("button", { name: "Use the guide's suggestion for HDR10: Prefer" }));
    expect(p.onFormatIntentChange).toHaveBeenCalledWith("hdr", "prefer");
    fireEvent.click(within(radios).getByRole("radio", { name: "Never" }));
    expect(p.onFormatIntentChange).toHaveBeenLastCalledWith("hdr", "blocked");
  });

  it("shows the answer the person gave, and no suggestion beside it", () => {
    show({ customFormatIds: ["hdr"], formatIntents: { hdr: "strong-prefer" } });
    fireEvent.click(step(/What should the picture be/));
    expect(screen.getByRole("radio", { name: "Strongly prefer" })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByRole("radio", { name: "Off" })).toHaveAttribute("aria-checked", "false");
    expect(screen.queryByRole("button", { name: /Use the guide's suggestion/ })).toBeNull();
  });

  it("makes no suggestion for a rule the guide scores at nothing", () => {
    show({ customFormatIds: ["lang"] });
    fireEvent.click(step(/Which language/));
    expect(screen.getByRole("radiogroup", { name: "How much this profile wants Original" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Use the guide's suggestion/ })).toBeNull();
  });

  it("turns a rule off again with the Off button, and says what each answer means", () => {
    const { p } = show({ customFormatIds: ["hdr"], formatIntents: { hdr: "prefer" } });
    fireEvent.click(step(/What should the picture be/));
    fireEvent.click(screen.getByRole("radio", { name: "Off" }));
    expect(p.onFormatIntentChange).toHaveBeenCalledWith("hdr", "neutral");
    const helps = screen.getAllByRole("radio").map((radio) => radio.getAttribute("title"));
    expect(helps).toEqual([
      "This rule does nothing in this profile. Every rule starts here until you turn it on.",
      "Deluno will never take a release that matches this.",
      "Taken only when nothing better is available.",
      "Tips a close call towards releases that match.",
      "Ranks the match first and can justify replacing a file you already have."
    ]);
    fireEvent.click(screen.getByRole("radio", { name: "Avoid" }));
    expect(p.onFormatIntentChange).toHaveBeenLastCalledWith("hdr", "avoid");
    fireEvent.click(screen.getByRole("radio", { name: "Strongly prefer" }));
    expect(p.onFormatIntentChange).toHaveBeenLastCalledWith("hdr", "strong-prefer");
  });

  it("marks the answer given and only that one", () => {
    show({ customFormatIds: ["hdr"], formatIntents: { hdr: "avoid" } });
    fireEvent.click(step(/What should the picture be/));
    const [off, never, avoid] = screen.getAllByRole("radio");
    expect(avoid.className).toContain("border-primary/40 bg-primary/10 text-primary");
    expect(off.className).toContain("border-transparent text-muted-foreground");
    expect(never.className).not.toContain("bg-primary/10");
    expect(avoid.className).toContain("h-[30px] shrink-0");
  });

  it("lists nothing when no rule of the step is chosen", () => {
    show({ customFormatIds: ["audio"] });
    fireEvent.click(step(/What should the picture be/));
    expect(screen.queryByRole("radiogroup")).toBeNull();
  });
});

describe("how the rows look", () => {
  it("outlines the open step and draws its chevron turned over", () => {
    show();
    const rows = screen.getAllByRole("listitem").filter((row) => row.querySelector(":scope > button"));
    expect(rows[0].className).toContain("border-ring/60");
    expect(rows[0].className).toContain("overflow-hidden rounded-[10px] border bg-surface-2/50 transition-colors");
    expect(rows[1].className).toContain("border-hairline");
    expect(rows[1].className).not.toContain("border-ring/60");
    const chevron = (row: Element) => row.querySelector("button")!.lastElementChild!;
    expect(chevron(rows[0])).toHaveClass("rotate-180");
    expect(chevron(rows[1])).not.toHaveClass("rotate-180");
    expect(chevron(rows[1])).toHaveClass("mt-1", "h-4", "w-4", "shrink-0", "text-muted-foreground", "transition-transform");
  });

  it("fills the rule chips that are on", () => {
    show({ customFormatIds: ["hdr"] });
    fireEvent.click(step(/What should the picture be/));
    expect(screen.getByRole("checkbox", { name: "HDR10" }).className).toContain("border-ring bg-ring/15 text-foreground");
    expect(screen.getByRole("checkbox", { name: "HDR10" }).className).toContain("rounded-full border px-2.5 py-1");
    expect(screen.getByRole("checkbox", { name: "Dolby Vision" }).className).toContain("border-hairline text-muted-foreground hover:border-ring/50 hover:text-foreground");
    expect(screen.getByRole("checkbox", { name: "Dolby Vision" }).className).not.toContain("bg-ring/15");
  });

  it("does the same for the person's own rules, and outlines their question when open", () => {
    show({ customFormatIds: ["mine1"] });
    fireEvent.click(step(/How should your own rules affect a choice/));
    expect(screen.getByRole("checkbox", { name: "My rule" }).className).toContain("border-ring bg-ring/15 text-foreground");
    expect(screen.getByRole("checkbox", { name: "My rule" }).className).toContain("rounded-full border px-2.5 py-1");
    expect(screen.getByRole("checkbox", { name: "Second rule" }).className).toContain("border-hairline text-muted-foreground hover:border-ring/50 hover:text-foreground");
    const row = screen.getByRole("checkbox", { name: "My rule" }).closest("[role='listitem']")!;
    const chevron = () => row.querySelector("button")!.lastElementChild!;
    expect(row.className).toContain("border-ring/60");
    expect(row.className).toContain("overflow-hidden rounded-[10px] border bg-surface-2/50 transition-colors");
    expect(chevron()).toHaveClass("rotate-180");
    expect(chevron()).toHaveClass("mt-1", "h-4", "w-4", "shrink-0", "text-muted-foreground", "transition-transform");
    fireEvent.click(step(/How should your own rules affect a choice/));
    expect(row.className).toContain("border-hairline");
    expect(chevron()).not.toHaveClass("rotate-180");
  });
});

describe("when the answers change under it", () => {
  it("offers the rules of the kind of library it is now, and what is chosen now", () => {
    const first = props({ customFormatIds: [] });
    const view = render(<QualityBuildSteps {...first} />);
    fireEvent.click(step(/What should the picture be/));
    expect(within(screen.getByRole("group", { name: "What should the picture be?" })).getAllByRole("checkbox").map((box) => box.textContent)).toEqual(["HDR10", "Dolby Vision"]);
    expect(screen.queryByRole("radiogroup")).toBeNull();
    view.rerender(<QualityBuildSteps {...first} customFormatIds={["hdr"]} />);
    expect(screen.getByRole("radiogroup", { name: "How much this profile wants HDR10" })).toBeInTheDocument();
    view.rerender(<QualityBuildSteps {...first} customFormatIds={["hdr"]} mediaType="tv" />);
    expect(within(screen.getByRole("group", { name: "What should the picture be?" })).getAllByRole("checkbox").map((box) => box.textContent)).toEqual(["TV HDR"]);
    fireEvent.click(step(/How should your own rules affect a choice/));
    expect(within(screen.getByRole("group", { name: "Your custom release rules" })).getAllByRole("checkbox").map((box) => box.textContent)).toEqual(["TV mine"]);
  });

  it("asks about the person's own rules only while the guide leaves some outside its steps", () => {
    const first = props();
    const hdr = [(formats as unknown as unknown[])[0]] as never;
    const view = render(<QualityBuildSteps {...first} guide={{ customFormats: [] } as never} customFormats={hdr} />);
    expect(screen.getByRole("button", { name: /How should your own rules affect a choice/ })).toBeInTheDocument();
    view.rerender(<QualityBuildSteps {...first} customFormats={hdr} />);
    expect(screen.queryByRole("button", { name: /How should your own rules affect a choice/ })).toBeNull();
  });
});

describe("the rules that are the person's own", () => {
  it("is a last question that names the ones chosen, up to two with and, then how many more", () => {
    show();
    expect(screen.getByText("No custom rules chosen")).toBeInTheDocument();
    cleanup();
    show({ customFormatIds: ["mine1"] });
    expect(screen.getByText("My rule")).toBeInTheDocument();
    cleanup();
    show({ customFormatIds: ["mine1", "mine2"] });
    expect(screen.getByText("My rule and Second rule")).toBeInTheDocument();
    cleanup();
    show({ customFormatIds: ["mine1", "mine2", "mine3"] });
    expect(screen.getByText("My rule, Second rule, and 1 more")).toBeInTheDocument();
  });

  it("names only the custom rules among the rules chosen, and none with no name", () => {
    show({ customFormatIds: ["hdr", "mine1", "blank"] });
    expect(screen.getByText("My rule")).toBeInTheDocument();
    fireEvent.click(step(/How should your own rules affect a choice/));
    expect(screen.getAllByRole("radiogroup").map((group) => group.getAttribute("aria-label"))).toEqual(["How much this profile wants My rule", "How much this profile wants "]);
  });

  it("is left out when every rule belongs to a question above", () => {
    show({ customFormats: (formats as unknown as Array<{ trashId: string | null }>).filter((item) => item.trashId) as never });
    expect(screen.queryByRole("button", { name: /How should your own rules affect a choice/ })).toBeNull();
    expect(within(screen.getByRole("list")).getAllByRole("listitem")).toHaveLength(7);
  });

  it("opens in place with the rules of this kind of library only, and saves the guide's suggestion when one is ticked", () => {
    const { p } = show();
    fireEvent.click(step(/How should your own rules affect a choice/));
    expect(step(/How should your own rules affect a choice/)).toHaveAttribute("aria-expanded", "true");
    expect(step(/How good, and when to stop/)).toHaveAttribute("aria-expanded", "false");
    const group = screen.getByRole("group", { name: "Your custom release rules" });
    expect(within(group).getAllByRole("checkbox").map((box) => box.textContent)).toEqual(["My rule", "Second rule", "Third rule", ""]);
    fireEvent.click(within(group).getByRole("checkbox", { name: "My rule" }));
    expect(p.onCustomFormatIdsChange).toHaveBeenCalledWith(["mine1"]);
    expect(p.onFormatIntentChange).toHaveBeenCalledWith("mine1", "prefer");
    fireEvent.click(step(/How should your own rules affect a choice/));
    expect(screen.queryByRole("group", { name: "Your custom release rules" })).toBeNull();
  });

  it("saves no answer for a custom rule that already has one, or when a rule is removed that has none", () => {
    const { p } = show({ customFormatIds: ["mine1"], formatIntents: { mine2: "avoid" } });
    fireEvent.click(step(/How should your own rules affect a choice/));
    const group = screen.getByRole("group", { name: "Your custom release rules" });
    fireEvent.click(within(group).getByRole("checkbox", { name: "Second rule" }));
    expect(p.onCustomFormatIdsChange).toHaveBeenLastCalledWith(["mine1", "mine2"]);
    fireEvent.click(within(group).getByRole("checkbox", { name: "My rule" }));
    expect(p.onCustomFormatIdsChange).toHaveBeenLastCalledWith([]);
    expect(p.onFormatIntentChange).not.toHaveBeenCalled();
  });

  it("keeps an answer already given, removes a rule on a second click, and asks how much it matters", () => {
    const { p } = show({ customFormatIds: ["mine1", "mine2"], formatIntents: { mine1: "avoid" } });
    fireEvent.click(step(/How should your own rules affect a choice/));
    const group = screen.getByRole("group", { name: "Your custom release rules" });
    fireEvent.click(within(group).getByRole("checkbox", { name: "My rule" }));
    expect(p.onCustomFormatIdsChange).toHaveBeenLastCalledWith(["mine2"]);
    fireEvent.click(within(group).getByRole("checkbox", { name: "Third rule" }));
    expect(p.onCustomFormatIdsChange).toHaveBeenLastCalledWith(["mine1", "mine2", "mine3"]);
    expect(p.onFormatIntentChange).toHaveBeenCalledWith("mine3", "neutral");
    expect(p.onFormatIntentChange).not.toHaveBeenCalledWith("mine1", expect.anything());
    expect(screen.getByRole("radiogroup", { name: "How much this profile wants My rule" })).toBeInTheDocument();
  });
});

describe("the release you try against your answers", () => {
  const field = () => screen.getByRole("textbox", { name: "Release name to judge" });

  it("starts with a film to judge, or an episode for a show, and says what Deluno would do", async () => {
    show();
    expect((field() as HTMLInputElement).value).toBe("Dune.2021.2160p.UHD.BluRay.REMUX.HDR.TrueHD.Atmos-FraMeSToR");
    expect(screen.getByText("Type a release name and Deluno will say what it would do with it.")).toBeInTheDocument();
    await settle();
    expect(screen.getByText("Deluno would take this and stop looking")).toBeInTheDocument();
    expect(screen.getByText("All targets are met")).toBeInTheDocument();
    expect(judge).toHaveBeenCalledWith(expect.objectContaining({ name: "Mine", mediaType: "movies", allowedQualities: [], cutoffQuality: "", customFormatIds: [], allowLowerQualityReplacements: false, upgradeUntilCutoff: true, upgradeUnknownItems: false }));
    cleanup();
    show({ mediaType: "tv" });
    expect((field() as HTMLInputElement).value).toBe("The.Expanse.S05E01.2160p.AMZN.WEB-DL.DDP5.1.HDR.HEVC-NTb");
  });

  it("waits for the typing to pause, and judges the newest name only", async () => {
    show();
    await settle();
    judge.mockClear();
    fireEvent.change(field(), { target: { value: "A.Release.2020" } });
    await act(async () => { await vi.advanceTimersByTimeAsync(300); });
    expect(judge).not.toHaveBeenCalled();
    fireEvent.change(field(), { target: { value: "B.Release.2021" } });
    await settle();
    expect(judge).toHaveBeenCalledTimes(1);
    expect(judge).toHaveBeenCalledWith(expect.objectContaining({ releaseName: "B.Release.2021" }));
  });

  it("clears the verdict when the name is emptied, and judges nothing", async () => {
    show();
    await settle();
    judge.mockClear();
    fireEvent.change(field(), { target: { value: "   " } });
    await settle();
    expect(judge).not.toHaveBeenCalled();
    expect(screen.getByText("Type a release name and Deluno will say what it would do with it.")).toBeInTheDocument();
  });

  it("draws a refusal in the colour of a problem, and a take in the colour of good news", async () => {
    judge.mockResolvedValue({ refusal: "No", candidateEvaluation: { status: "meetsPlan", reasons: [] } });
    show();
    await settle();
    const refusedClass = screen.getByText("Deluno would not take this").className;
    cleanup();
    judge.mockResolvedValue({ candidateEvaluation: { status: "meetsPlan", reasons: [] } });
    show();
    await settle();
    expect(screen.getByText("Deluno would take this and stop looking").className).not.toBe(refusedClass);
    expect(document.querySelectorAll("ul li")).toHaveLength(0);
  });

  it("draws a refusal exactly as the problem colour of a chip", async () => {
    const { container } = render(<Chip tone="bad">Deluno would not take this</Chip>);
    const bad = container.firstElementChild!.className;
    cleanup();
    judge.mockResolvedValue({ refusal: "No" });
    show();
    await settle();
    expect(screen.getByText("Deluno would not take this").className).toBe(bad);
  });

  it("lists no reasons when the judgement gives none at all", async () => {
    judge.mockResolvedValue({ candidateEvaluation: { status: "meetsPlan" } });
    show();
    await settle();
    expect(screen.getByText("Deluno would take this and stop looking")).toBeInTheDocument();
    expect(document.querySelectorAll("ul li")).toHaveLength(0);
  });

  it("says why a release would not be taken, and calls it a refusal", async () => {
    judge.mockResolvedValue({ refusal: "Not in the tiers you allow", candidateEvaluation: { status: "meetsPlan", reasons: ["ignored"] } });
    show();
    await settle();
    expect(screen.getByText("Deluno would not take this")).toBeInTheDocument();
    expect(screen.getByText("Not in the tiers you allow")).toBeInTheDocument();
    expect(screen.queryByText("ignored")).toBeNull();
  });

  it("gives at most four reasons, and none when there are none", async () => {
    judge.mockResolvedValue({ candidateEvaluation: { status: "belowGoal", reasons: ["r1", "r2", "r3", "r4", "r5"] } });
    show();
    await settle();
    expect(screen.getByText("Deluno would take this and keep looking for better")).toBeInTheDocument();
    expect(screen.getAllByText(/^r\d$/).map((node) => node.textContent)).toEqual(["r1", "r2", "r3", "r4"]);
    cleanup();
    judge.mockResolvedValue({});
    show();
    await settle();
    expect(screen.getByText("Deluno could not say")).toBeInTheDocument();
  });

  it("says it could not judge when the server fails, and judges again on the next change", async () => {
    judge.mockRejectedValueOnce(new Error("down"));
    show();
    await settle();
    expect(screen.getByText("Deluno could not judge that just now. Change an answer to try again.")).toBeInTheDocument();
    fireEvent.change(field(), { target: { value: "Another.Release" } });
    await settle();
    expect(screen.queryByText(/could not judge that just now/)).toBeNull();
    expect(screen.getByText("Deluno would take this and stop looking")).toBeInTheDocument();
  });

  it("ignores an answer that comes after a newer question was asked", async () => {
    let first: (value: unknown) => void = () => undefined;
    judge.mockImplementationOnce(() => new Promise((resolve) => { first = resolve; }));
    judge.mockResolvedValue({ candidateEvaluation: { status: "missing", reasons: ["newest"] } });
    show();
    await act(async () => { await vi.advanceTimersByTimeAsync(400); });
    fireEvent.change(field(), { target: { value: "Newer.Release" } });
    await settle();
    expect(screen.getByText("newest")).toBeInTheDocument();
    await act(async () => { first({ candidateEvaluation: { status: "meetsPlan", reasons: ["stale"] } }); });
    expect(screen.queryByText("stale")).toBeNull();
    expect(screen.getByText("Deluno would refuse this")).toBeInTheDocument();
  });

  it("ignores a failure that comes after a newer question was asked", async () => {
    let first: (error: unknown) => void = () => undefined;
    judge.mockImplementationOnce(() => new Promise((_, reject) => { first = reject; }));
    show();
    await act(async () => { await vi.advanceTimersByTimeAsync(400); });
    fireEvent.change(field(), { target: { value: "Newer.Release" } });
    await settle();
    await act(async () => { first(new Error("late")); });
    expect(screen.queryByText(/could not judge that just now/)).toBeNull();
  });

  it("judges the answers as they stand, in the custom rules question too", async () => {
    show({ customFormatIds: ["mine1"], formatIntents: { mine1: "prefer" } });
    fireEvent.click(step(/How should your own rules affect a choice/));
    await settle();
    expect(screen.getByRole("textbox", { name: "Release name to judge" })).toBeInTheDocument();
    expect(judge).toHaveBeenLastCalledWith(expect.objectContaining({ customFormatIds: ["mine1"], formatIntents: { mine1: "prefer" } }));
  });
});
