import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot, type GuidePackage } from "../lib/api";

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

/**
 * The "Applies to" switch of the New custom rule drawer (#1329): TRaSH's movies copy and TV copy of a format are different formats
 * with different ids, so switching Movies/TV moves a chosen guide format to the other media type's copy, or clears the choice and
 * the name when TRaSH has no copy for that media type.
 */
const format = (trashId: string, name: string, mediaTypes: string[], score: number) => ({
  trashId, name, category: "streaming", description: `${name} (${mediaTypes.join("/")})`, originalScore: score,
  patterns: [], sourceMatcherClauses: [{ implementation: "ReleaseTitleSpecification", name: "x", negate: false, required: true, fields: { value: "x" } }], mediaTypes, bundleOnly: false, mappingStatus: "reviewed", mappedTraitIds: [], sourceKind: "trash-guides"
});
const guide = {
  id: "guide", name: "Test guide", version: 1, schemaVersion: 3,
  source: { sourceName: "test", repositoryUrl: "", guideUrl: "", upstreamRevision: "1", reviewedUtc: "", adaptation: "" },
  integritySha256: "", qualityTiers: [],
  customFormats: [
    format("amzn-movies", "AMZN", ["movies"], 100),
    format("amzn-tv", "AMZN", ["tv"], 150),
    format("movies-only", "Movies Only Thing", ["movies"], 40)
  ],
  qualityProfiles: [], bundles: [],
  sourceInventory: { schemaVersion: 2, upstreamRevision: "1", customFormats: [], qualityProfiles: [], formatGroups: [] }
} as unknown as GuidePackage;

const loaderData = {
  libraries: [], policySets: [], qualityProfiles: [], customFormats: [], settings: emptyPlatformSettingsSnapshot, guide,
  guideUpdateCheck: { isEnabled: false, lastCheckedUtc: null, lastSeenRevision: null, status: "disabled", error: null, report: null, updatedUtc: new Date(0).toISOString() },
  guideVersions: [], ruleMigration: null
};
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: vi.fn() })
}));

const { SettingsCustomFormatsPage } = await import("./settings-custom-formats-page");

afterEach(() => cleanup());

async function openNewRule() {
  render(<MemoryRouter><SettingsCustomFormatsPage /></MemoryRouter>);
  fireEvent.click(screen.getAllByRole("button", { name: "New custom rule" })[0]!);
  return await screen.findByRole("dialog");
}
const appliesTo = (dialog: HTMLElement, label: "Movies" | "TV") => within(within(dialog).getByRole("radiogroup", { name: "Applies to" })).getByRole("radio", { name: label });

describe("switching Applies to in the New custom rule drawer", () => {
  it("moves a chosen guide format to the other media type's copy with that copy's own score, and back", async () => {
    const dialog = await openNewRule();
    fireEvent.change(within(dialog).getByRole("combobox", { name: /Guide choice/ }), { target: { value: "amzn-movies" } });
    expect(within(dialog).getByRole("textbox", { name: /Name/ })).toHaveValue("AMZN");

    fireEvent.click(appliesTo(dialog, "TV"));
    expect((within(dialog).getByRole("combobox", { name: /Guide choice/ }) as HTMLSelectElement).value).toBe("amzn-tv");
    expect(within(dialog).getByText("AMZN (tv)")).toBeInTheDocument();

    fireEvent.click(appliesTo(dialog, "Movies"));
    expect((within(dialog).getByRole("combobox", { name: /Guide choice/ }) as HTMLSelectElement).value).toBe("amzn-movies");
  });

  it("clears the guide choice and the name when TRaSH has no copy for the other media type", async () => {
    const dialog = await openNewRule();
    fireEvent.change(within(dialog).getByRole("combobox", { name: /Guide choice/ }), { target: { value: "movies-only" } });
    expect(within(dialog).getByRole("textbox", { name: /Name/ })).toHaveValue("Movies Only Thing");

    fireEvent.click(appliesTo(dialog, "TV"));

    expect((within(dialog).getByRole("combobox", { name: /Guide choice/ }) as HTMLSelectElement).value).toBe("");
    expect(within(dialog).getByRole("textbox", { name: /Name/ })).toHaveValue("");
  });

  it("with no guide format chosen, switching only changes the media type", async () => {
    const dialog = await openNewRule();
    fireEvent.change(within(dialog).getByRole("textbox", { name: /Name/ }), { target: { value: "My own rule" } });

    fireEvent.click(appliesTo(dialog, "TV"));

    expect(within(dialog).getByRole("textbox", { name: /Name/ })).toHaveValue("My own rule");
    expect(appliesTo(dialog, "TV")).toHaveAttribute("aria-checked", "true");
  });
});
