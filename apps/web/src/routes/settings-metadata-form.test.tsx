import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";

/**
 * Settings > Metadata: the Details language and Ratings region pickers, Save metadata settings (the footer states and
 * the PATCH), a value saved elsewhere reaching the open form, and the Saved keys card for a key that cannot be read.
 */

vi.setConfig({ testTimeout: 30_000 });
const authedFetchMock = vi.hoisted(() => vi.fn());
vi.mock("../lib/use-auth", () => ({ authedFetch: authedFetchMock }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
const liveRefresh = vi.hoisted(() => ({ spec: undefined as { events?: string[] } | undefined }));
vi.mock("../lib/live-refresh", () => ({ useLiveRefresh: (spec: { events?: string[] }) => { liveRefresh.spec = spec; } }));

const loaderData = vi.hoisted(() => ({ settings: {} as Record<string, unknown>, metadataStatus: null as { mode: string } | null }));
const revalidateMock = vi.fn();
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate: revalidateMock, state: "idle" })
}));

const { SettingsMetadataPage } = await import("./settings-metadata-page");
const renderPage = () => render(<MemoryRouter><SettingsMetadataPage /></MemoryRouter>);
const respond = (body: unknown = {}, status = 200) => ({ ok: status < 400, status, json: async () => body, text: async () => JSON.stringify(body), headers: new Headers() });
const optionLabels = (select: HTMLElement) => [...(select as HTMLSelectElement).options].map((option) => option.textContent);

beforeEach(() => {
  cleanup();
  authedFetchMock.mockReset();
  authedFetchMock.mockResolvedValue(respond({}));
  revalidateMock.mockReset();
  loaderData.settings = { ...emptyPlatformSettingsSnapshot, metadataLanguage: "en", metadataCertificationCountry: "AU" };
  loaderData.metadataStatus = null;
});

describe("the language and region pickers", () => {
  it("offers English, en-AU, en-GB, German, French, Spanish, Japanese and a way to type any other language code", () => {
    renderPage();
    const labels = optionLabels(screen.getByLabelText("Details language"));
    expect(labels).toEqual([
      "English (en)", "English — Australia (en-AU)", "English — United Kingdom (en-GB)", "German (de)", "French (fr)", "Spanish (es)", "Japanese (ja)", "Other language code"
    ]);
    fireEvent.change(screen.getByLabelText("Details language"), { target: { value: "__custom" } });
    expect(screen.getByPlaceholderText("IETF language tag, e.g. pt-BR")).toBeInTheDocument();
  });

  it("offers AU, US, GB, CA, NZ, DE, FR, JP and a way to type any other country code", () => {
    renderPage();
    expect(optionLabels(screen.getByLabelText("Ratings region"))).toEqual([
      "Australia (AU)", "United States (US)", "United Kingdom (GB)", "Canada (CA)", "New Zealand (NZ)", "Germany (DE)", "France (FR)", "Japan (JP)", "Other country code"
    ]);
    fireEvent.change(screen.getByLabelText("Ratings region"), { target: { value: "__custom" } });
    expect(screen.getByPlaceholderText("ISO country code, e.g. NL")).toBeInTheDocument();
  });

  it("shows a saved language or region that is not in the list as the typed 'other' code, not as a blank", () => {
    loaderData.settings = { ...emptyPlatformSettingsSnapshot, metadataLanguage: "pt-BR", metadataCertificationCountry: "NL" };
    renderPage();
    expect(screen.getByLabelText("Details language")).toHaveValue("__custom");
    expect(screen.getByPlaceholderText("IETF language tag, e.g. pt-BR")).toHaveValue("pt-BR");
    expect(screen.getByPlaceholderText("ISO country code, e.g. NL")).toHaveValue("NL");
  });
});

describe("Save metadata settings", () => {
  it("goes from Unsaved changes to Saving to Saved just now and PATCHes both fields in one call", async () => {
    let finish: (value: unknown) => void = () => undefined;
    authedFetchMock.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    renderPage();
    expect(screen.getByRole("button", { name: "Save metadata settings" })).toBeDisabled();
    fireEvent.change(screen.getByLabelText("Details language"), { target: { value: "de" } });
    fireEvent.change(screen.getByLabelText("Ratings region"), { target: { value: "DE" } });
    expect(screen.getByText("Unsaved changes")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Save metadata settings" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalled());
    expect(screen.getByText("Saving…")).toBeInTheDocument();
    finish(respond({ metadataLanguage: "de", metadataCertificationCountry: "DE" }));
    await waitFor(() => expect(screen.getByText("Saved just now")).toBeInTheDocument());
    const [url, init] = authedFetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/settings");
    expect(init.method).toBe("PATCH");
    expect(JSON.parse(init.body as string)).toEqual({ metadataCertificationCountry: "DE", metadataLanguage: "de" });
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("shows the server's refusal in the footer and keeps the edit", async () => {
    authedFetchMock.mockResolvedValue(respond({ errors: { metadataLanguage: ["Use a language tag such as en-AU."] } }, 400));
    renderPage();
    fireEvent.change(screen.getByLabelText("Details language"), { target: { value: "__custom" } });
    fireEvent.change(screen.getByPlaceholderText("IETF language tag, e.g. pt-BR"), { target: { value: "nonsense!" } });
    fireEvent.click(screen.getByRole("button", { name: "Save metadata settings" }));
    await waitFor(() => expect(screen.queryByText("Saving…")).toBeNull());
    expect(screen.queryByText("Saved just now")).toBeNull();
    expect(screen.getByPlaceholderText("IETF language tag, e.g. pt-BR")).toHaveValue("nonsense!");
  });
});

describe("staying current", () => {
  it("reads again when a setting changes elsewhere", () => {
    renderPage();
    expect(liveRefresh.spec?.events).toEqual(["SettingsChanged"]);
  });

  it("follows a value saved elsewhere into the form while nothing here is edited", () => {
    const view = renderPage();
    expect(screen.getByLabelText("Details language")).toHaveValue("en");
    loaderData.settings = { ...emptyPlatformSettingsSnapshot, metadataLanguage: "fr", metadataCertificationCountry: "AU" };
    view.rerender(<MemoryRouter><SettingsMetadataPage /></MemoryRouter>);
    expect(screen.getByLabelText("Details language")).toHaveValue("fr");
  });

  it("does not overwrite an edit in progress when a value is saved elsewhere", () => {
    const view = renderPage();
    fireEvent.change(screen.getByLabelText("Details language"), { target: { value: "ja" } });
    loaderData.settings = { ...emptyPlatformSettingsSnapshot, metadataLanguage: "fr", metadataCertificationCountry: "AU" };
    view.rerender(<MemoryRouter><SettingsMetadataPage /></MemoryRouter>);
    expect(screen.getByLabelText("Details language")).toHaveValue("ja");
  });
});

describe("Saved keys", () => {
  it("appears only for a stored key that cannot be read here, asks for just that key and sends only what was typed", async () => {
    renderPage();
    expect(screen.queryByText(/saved keys/i)).toBeNull();
    cleanup();
    loaderData.settings = { ...emptyPlatformSettingsSnapshot, metadataLanguage: "en", metadataCertificationCountry: "AU", metadataOmdbApiKeyUnreadable: true, metadataKeyProblem: "This key was saved on another computer." };
    renderPage();
    expect(screen.getByText(/saved keys/i)).toBeInTheDocument();
    expect(screen.getByLabelText("OMDb API key")).toBeInTheDocument();
    expect(screen.queryByLabelText("TMDb API key")).toBeNull();
    expect(screen.getByRole("button", { name: "Save keys" })).toBeDisabled();
    fireEvent.change(screen.getByLabelText("OMDb API key"), { target: { value: "omdb-key-1" } });
    fireEvent.click(screen.getByRole("button", { name: "Save keys" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalled());
    expect(JSON.parse((authedFetchMock.mock.calls[0][1] as RequestInit).body as string)).toEqual({ metadataOmdbApiKey: "omdb-key-1" });
  });
});
