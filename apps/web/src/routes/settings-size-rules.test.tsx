import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { QualityModelSnapshot } from "../lib/api";

/**
 * Size rules: each tier's range slider and its Min, Preferred and Max boxes, 0 as no limit, the same-quality switch, saving,
 * and a model changed elsewhere.
 */

vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: () => undefined }));
const calls: Array<{ path: string; method: string; body: unknown }> = [];
let model: QualityModelSnapshot;
let refuseWith: string | null = null;
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  fetchJson: async (path: string, init?: { method?: string; body?: string }) => {
    calls.push({ path, method: init?.method ?? "GET", body: init?.body ? JSON.parse(init.body) : undefined });
    if (refuseWith) throw new Error(refuseWith);
    return init?.body ? { ...model, ...JSON.parse(init.body) } : model;
  }
}));
const loaderData: { qualityModel: QualityModelSnapshot } = { qualityModel: undefined as never };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData
}));

const { SettingsQualityPage } = await import("./settings-quality-page-v2");

const tier = (name: string, rank: number, extra: Partial<QualityModelSnapshot["tiers"][number]> = {}) => ({
  name, rank, movieMinGb: 2, moviePreferredGb: 5, movieMaxGb: 10, episodeMinMb: 200, episodePreferredMb: 800, episodeMaxMb: 2000, scoreCeiling: 0, ...extra
});
const freshModel = (): QualityModelSnapshot => ({
  version: "test",
  tiers: [tier("WEB 1080p", 70), tier("Bluray 2160p", 100, { movieMinGb: 0, movieMaxGb: 0 }), tier("WEB 480p", 20)],
  upgradeStop: { stopWhenCutoffMet: true, requireCustomFormatGainForSameQuality: true },
  updatedUtc: new Date(0).toISOString()
});
const view = () => <MemoryRouter><SettingsQualityPage /></MemoryRouter>;
const puts = () => calls.filter((call) => call.method === "PUT");

beforeEach(() => {
  calls.length = 0;
  refuseWith = null;
  model = freshModel();
  loaderData.qualityModel = model;
});

describe("size rules", () => {
  it("shows a slider and Min, Preferred and Max boxes for each tier, and says any size for a tier at 0 and 0", () => {
    render(view());
    expect(screen.getByRole("slider", { name: "WEB 1080p movie minimum" })).toBeTruthy();
    expect(screen.getByRole("slider", { name: "WEB 1080p movie maximum" })).toBeTruthy();
    expect((screen.getByRole("spinbutton", { name: "WEB 1080p movie minimum value" }) as HTMLInputElement).value).toBe("2");
    expect((screen.getByRole("spinbutton", { name: "WEB 1080p movie preferred value" }) as HTMLInputElement).value).toBe("5");
    expect((screen.getByRole("spinbutton", { name: "WEB 1080p movie maximum value" }) as HTMLInputElement).value).toBe("10");
    expect(screen.getByText(/Rank 100 · any size/)).toBeTruthy();
    expect(screen.getByText(/0 and 0 means no limit/)).toBeTruthy();
  });

  it("typing a size or moving a slider makes the page changed, and Save sends every tier in one PUT and says Saved just now", async () => {
    render(view());
    const save = screen.getByRole("button", { name: "Save size rules" });
    expect(save).toBeDisabled();

    fireEvent.change(screen.getByRole("spinbutton", { name: "WEB 1080p movie maximum value" }), { target: { value: "12" } });
    fireEvent.keyDown(screen.getByRole("slider", { name: "WEB 1080p movie minimum" }), { key: "ArrowRight" });
    expect(save).toBeEnabled();
    fireEvent.click(save);

    await waitFor(() => expect(puts()).toHaveLength(1));
    expect(puts()[0].path).toBe("/api/quality-model");
    const body = puts()[0].body as { tiers: Array<{ name: string; movieMinGb: number; movieMaxGb: number }>; upgradeStop: unknown };
    expect(body.tiers.map((item) => item.name)).toEqual(["WEB 1080p", "Bluray 2160p", "WEB 480p"]);
    expect(body.tiers[0]).toMatchObject({ movieMinGb: 2.1, movieMaxGb: 12 });
    expect(await screen.findByText("Saved just now")).toBeTruthy();
  });

  it("shows TV sizes in MB when the scope is switched to TV", () => {
    render(view());
    fireEvent.click(screen.getByRole("radio", { name: "TV" }));
    expect(screen.getByRole("spinbutton", { name: "WEB 1080p episode maximum value" })).toBeTruthy();
    expect((screen.getByRole("spinbutton", { name: "WEB 1080p episode preferred value" }) as HTMLInputElement).value).toBe("800");
  });

  it("the same-quality switch is saved with the size rules", async () => {
    render(view());
    const gain = screen.getByRole("switch", { name: /Require a preference improvement at the same quality/ });
    expect(gain.getAttribute("aria-checked")).toBe("true");
    fireEvent.click(gain);
    fireEvent.click(screen.getByRole("button", { name: "Save size rules" }));

    await waitFor(() => expect(puts()).toHaveLength(1));
    expect(puts()[0].body).toMatchObject({ upgradeStop: { stopWhenCutoffMet: true, requireCustomFormatGainForSameQuality: false } });
  });

  it("shows the server's words in the footer when the size rules cannot be saved", async () => {
    render(view());
    fireEvent.change(screen.getByRole("spinbutton", { name: "WEB 1080p movie maximum value" }), { target: { value: "12" } });
    refuseWith = "A tier cannot have a smallest size above its largest.";
    fireEvent.click(screen.getByRole("button", { name: "Save size rules" }));
    expect(await screen.findByText("A tier cannot have a smallest size above its largest.")).toBeTruthy();
    expect(screen.queryByText("Saved just now")).toBeNull();
    expect((screen.getByRole("spinbutton", { name: "WEB 1080p movie maximum value" }) as HTMLInputElement).value).toBe("12");
  });

  it("shows a size changed elsewhere in the open page once it reads the model again (the page copies the loader's model once and is never re-synced)", () => {
    const rendered = render(view());
    expect((screen.getByRole("spinbutton", { name: "WEB 1080p movie maximum value" }) as HTMLInputElement).value).toBe("10");
    loaderData.qualityModel = { ...freshModel(), tiers: [tier("WEB 1080p", 70, { movieMaxGb: 33 }), tier("Bluray 2160p", 100), tier("WEB 480p", 20)] };
    rendered.rerender(view());
    expect((within(document.body).getByRole("spinbutton", { name: "WEB 1080p movie maximum value" }) as HTMLInputElement).value).toBe("33");
  });
});
