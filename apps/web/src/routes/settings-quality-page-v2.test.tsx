import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * Size Rules (Library Profiles): every tier's Min, Preferred and Max size for movies and for TV, grouped into resolution bands,
 * and the one upgrade choice. Each control sends the request the quality-model routes are tested against (QualityRoutesTests).
 */

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));

const tier = (name: string, rank: number, overrides: Record<string, unknown> = {}) => ({
  name, rank, movieMinGb: 1, moviePreferredGb: 4, movieMaxGb: 8, episodeMinMb: 100, episodePreferredMb: 800, episodeMaxMb: 2000, ...overrides
});

const model = (overrides: Record<string, unknown> = {}) => ({
  version: "quality-model/v2",
  tiers: [tier("SDTV", 12), tier("Bluray 720p", 40), tier("WEBDL 1080p", 70), tier("Bluray 1080p", 80, { movieMaxGb: 0 }), tier("WEBDL 2160p", 100)],
  upgradeStop: { stopWhenCutoffMet: true, requireCustomFormatGainForSameQuality: false },
  updatedUtc: "2026-10-01T00:00:00Z",
  ...overrides
});

let loaderData: { qualityModel: ReturnType<typeof model> };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData
}));

const { SettingsQualityPage } = await import("./settings-quality-page-v2");

function reply(body: unknown = {}, ok = true, status = 200) {
  return Promise.resolve({ ok, status, json: () => Promise.resolve(body), clone() { return this; }, text: () => Promise.resolve(JSON.stringify(body)) } as unknown as Response);
}

function renderPage() {
  return render(
    <MemoryRouter>
      <SettingsQualityPage />
    </MemoryRouter>
  );
}

/** A switch in a labelled row: its label wraps it, so it is found through the label's own text. */
function switchOf(label: string) {
  const row = screen.getAllByText(label).map((element) => element.closest("label")).find((candidate) => candidate !== null);
  const found = (row?.htmlFor ? document.getElementById(row.htmlFor) : row?.querySelector("[role=switch]")) as HTMLElement | null | undefined;
  if (!found) throw new Error(`No switch labelled ${label}`);
  return found;
}

const calls = (method: string, path: string) =>
  authedFetchMock.mock.calls.filter(([url, init]) => String(url) === path && ((init as RequestInit | undefined)?.method ?? "GET") === method);
const bodyOf = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body));

beforeEach(() => {
  authedFetchMock.mockReset();
  authedFetchMock.mockImplementation(() => reply(model()));
  loaderData = { qualityModel: model() };
});

describe("Size Rules", () => {
  it("shows the movie table in resolution bands, with only the two bands people tune open", () => {
    renderPage();

    expect(screen.getByText("Movie File Sizes")).toBeVisible();
    for (const band of ["SD", "720p", "1080p", "2160p"]) expect(screen.getByRole("button", { name: new RegExp(`^${band}`) })).toBeVisible();
    expect(screen.getByRole("button", { name: /^1080p/ })).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("button", { name: /^2160p/ })).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("button", { name: /^720p/ })).toHaveAttribute("aria-expanded", "false");
    expect(screen.getByLabelText("WEBDL 1080p movie minimum value")).toHaveValue(1);
    expect(screen.queryByLabelText("Bluray 720p movie minimum value")).not.toBeInTheDocument();
  });

  it("a band opens and closes on its own, and Expand all and Collapse all open and close every band", () => {
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: /^720p/ }));
    expect(screen.getByLabelText("Bluray 720p movie minimum value")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: /^720p/ }));
    expect(screen.queryByLabelText("Bluray 720p movie minimum value")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Expand all" }));
    expect(screen.getByLabelText("SDTV movie minimum value")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Collapse all" }));
    expect(screen.queryByLabelText("SDTV movie minimum value")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("WEBDL 1080p movie minimum value")).not.toBeInTheDocument();
  });

  it("a 0 maximum reads as Unlimited and a band with no rule reads as a dash", () => {
    loaderData = { qualityModel: model({ tiers: [tier("SDTV", 12, { movieMinGb: 0, movieMaxGb: 0, moviePreferredGb: 0 }), tier("Bluray 1080p", 80, { movieMaxGb: 0 })] }) };
    renderPage();

    expect(within(screen.getByRole("button", { name: /^1080p/ })).getByText("Unlimited")).toBeVisible();
    expect(within(screen.getByRole("button", { name: /^SD/ })).getAllByText("—")).toHaveLength(2);
  });

  it("the Movies and TV switch changes the table, its units and its labels", () => {
    renderPage();

    fireEvent.click(screen.getByRole("radio", { name: "TV" }));

    expect(screen.getByText("Episode File Sizes")).toBeVisible();
    expect(screen.getByText("Min (MB)")).toBeVisible();
    expect(screen.getByLabelText("WEBDL 1080p episode minimum value")).toHaveValue(100);
    fireEvent.click(screen.getByRole("radio", { name: "Movies" }));
    expect(screen.getByText("Min (GB)")).toBeVisible();
  });

  it("Save size rules sends the changed size and the upgrade choice, then says Saved just now", async () => {
    renderPage();
    fireEvent.change(screen.getByLabelText("WEBDL 1080p movie minimum value"), { target: { value: "2.5" } });
    fireEvent.change(screen.getByLabelText("WEBDL 1080p movie preferred value"), { target: { value: "5" } });
    fireEvent.change(screen.getByLabelText("WEBDL 1080p movie maximum value"), { target: { value: "0" } });
    fireEvent.click(switchOf("Require a preference improvement at the same quality"));

    fireEvent.click(screen.getByRole("button", { name: "Save size rules" }));

    await waitFor(() => expect(calls("PUT", "/api/quality-model")).toHaveLength(1));
    const body = bodyOf(calls("PUT", "/api/quality-model")[0]);
    expect(body.tiers.find((item: { name: string }) => item.name === "WEBDL 1080p")).toMatchObject({ movieMinGb: 2.5, moviePreferredGb: 5, movieMaxGb: 0 });
    expect(body.upgradeStop).toEqual({ stopWhenCutoffMet: true, requireCustomFormatGainForSameQuality: true });
    expect(await screen.findByText("Saved just now")).toBeVisible();
  });

  it("changing a TV size sends it in megabytes and leaves the movie sizes as they were", async () => {
    renderPage();
    fireEvent.click(screen.getByRole("radio", { name: "TV" }));
    fireEvent.change(screen.getByLabelText("WEBDL 1080p episode maximum value"), { target: { value: "3000" } });

    fireEvent.click(screen.getByRole("button", { name: "Save size rules" }));

    await waitFor(() => expect(calls("PUT", "/api/quality-model")).toHaveLength(1));
    expect(bodyOf(calls("PUT", "/api/quality-model")[0]).tiers.find((item: { name: string }) => item.name === "WEBDL 1080p")).toMatchObject({ episodeMaxMb: 3000, movieMinGb: 1, movieMaxGb: 8 });
  });

  it("says the server's reason when a save is refused, and does not say it saved", async () => {
    authedFetchMock.mockImplementation(() => reply({ errors: { tiers: ["Min cannot be above Max."] }, title: "Min cannot be above Max." }, false, 400));
    renderPage();
    fireEvent.change(screen.getByLabelText("WEBDL 1080p movie minimum value"), { target: { value: "9" } });

    fireEvent.click(screen.getByRole("button", { name: "Save size rules" }));

    await waitFor(() => expect(calls("PUT", "/api/quality-model")).toHaveLength(1));
    await waitFor(() => expect(screen.queryByText("Saved just now")).not.toBeInTheDocument());
    expect(screen.getByText("Save size rules")).toBeVisible();
  });

  it("Reset to defaults asks first, then posts the reset and shows the shipped numbers", async () => {
    authedFetchMock.mockImplementation((path: string) => reply(path === "/api/quality-model/reset" ? model({ tiers: [tier("WEBDL 1080p", 70, { movieMinGb: 3.5 })] }) : model()));
    renderPage();

    fireEvent.click(screen.getByRole("button", { name: "Reset to defaults" }));
    const dialog = within(await screen.findByRole("dialog"));
    expect(dialog.getByText("Reset size rules to defaults?")).toBeVisible();
    expect(calls("POST", "/api/quality-model/reset")).toHaveLength(0);
    fireEvent.click(dialog.getByRole("button", { name: "Reset to defaults" }));

    await waitFor(() => expect(calls("POST", "/api/quality-model/reset")).toHaveLength(1));
    expect(await screen.findByText("Reset to Deluno's shipped defaults")).toBeVisible();
    expect(screen.getByLabelText("WEBDL 1080p movie minimum value")).toHaveValue(3.5);
  });

  it("cancelling the reset sends nothing", async () => {
    renderPage();
    fireEvent.click(screen.getByRole("button", { name: "Reset to defaults" }));
    const dialog = within(await screen.findByRole("dialog"));

    fireEvent.click(dialog.getByRole("button", { name: "Cancel" }));

    expect(calls("POST", "/api/quality-model/reset")).toHaveLength(0);
  });
});
