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

const { SettingsMetadataPage, settingsMetadataLoader } = await import("./settings-metadata-page");
const renderPage = () => render(<MemoryRouter><SettingsMetadataPage /></MemoryRouter>);
const respond = (body: unknown = {}, status = 200) => ({ ok: status < 400, status, json: async () => body, text: async () => JSON.stringify(body), headers: new Headers(), clone() { return this; } });
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

  it("puts the server's sentence for a made-up language under the box (#1438)", async () => {
    const sentence = "Details language must be a language code such as en, de or pt-BR.";
    authedFetchMock.mockResolvedValue(respond({ errors: { metadataLanguage: [sentence] } }, 400));
    renderPage();
    fireEvent.change(screen.getByLabelText("Details language"), { target: { value: "__custom" } });
    fireEvent.change(screen.getByPlaceholderText("IETF language tag, e.g. pt-BR"), { target: { value: "xx-banana" } });
    fireEvent.click(screen.getByRole("button", { name: "Save metadata settings" }));
    expect((await screen.findAllByText(sentence)).length).toBeGreaterThan(0);
    expect(screen.queryByText("Saved just now")).toBeNull();
  });

  it("sends a real code such as pt-BR and shows no refusal (#1438)", async () => {
    authedFetchMock.mockResolvedValue(respond({ metadataLanguage: "pt-BR", metadataCertificationCountry: "AU" }));
    renderPage();
    fireEvent.change(screen.getByLabelText("Details language"), { target: { value: "__custom" } });
    fireEvent.change(screen.getByPlaceholderText("IETF language tag, e.g. pt-BR"), { target: { value: "pt-BR" } });
    fireEvent.click(screen.getByRole("button", { name: "Save metadata settings" }));
    await waitFor(() => expect(screen.queryByText("Saving…")).toBeNull());
    expect(screen.queryByText(/must be a language code/)).toBeNull();
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

describe("an edit made the moment a save finishes (#1503)", () => {
  it("is kept, instead of being put back to the old settings while Saved just now stays", async () => {
    renderPage();
    const language = screen.getByLabelText("Details language");
    fireEvent.change(language, { target: { value: "fr" } });
    let edited = false;
    const observer = new MutationObserver(() => {
      if (edited || !screen.queryByText("Saved just now")) return;
      edited = true;
      fireEvent.change(language, { target: { value: "ja" } });
    });
    observer.observe(document.body, { childList: true, subtree: true, characterData: true });
    fireEvent.click(screen.getByRole("button", { name: "Save metadata settings" }));
    await waitFor(() => expect(edited).toBe(true));
    observer.disconnect();
    await waitFor(() => expect(screen.getByText("Unsaved changes")).toBeVisible());
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

describe("Saved keys notice (C.11.9)", () => {
  it("says to enter the key again with the server's reason when a stored key cannot be read here, and shows nothing otherwise", () => {
    loaderData.settings = { ...emptyPlatformSettingsSnapshot, metadataLanguage: "en", metadataCertificationCountry: "AU" };
    renderPage();
    expect(screen.queryByText("Enter the saved key again")).toBeNull();
    cleanup();
    loaderData.settings = { ...emptyPlatformSettingsSnapshot, metadataLanguage: "en", metadataCertificationCountry: "AU", metadataTmdbApiKeyUnreadable: true, metadataKeyProblem: "The TMDb key was saved on another computer." };
    renderPage();
    const notice = screen.getByRole("alert");
    expect(notice).toHaveTextContent("Enter the saved key again");
    expect(notice).toHaveTextContent("The TMDb key was saved on another computer.");
  });
});

describe("the page loader", () => {
  it("reads the settings and the metadata status together", async () => {
    authedFetchMock.mockImplementation(async (path: string) => respond(path === "/api/settings" ? { metadataLanguage: "de" } : { mode: "broker" }));
    const data = await settingsMetadataLoader();
    expect(data.settings).toMatchObject({ metadataLanguage: "de" });
    expect(data.metadataStatus).toEqual({ mode: "broker" });
  });

  it("still opens, claiming nothing about the managed service, when the status cannot be read", async () => {
    authedFetchMock.mockImplementation(async (path: string) => respond(path === "/api/settings" ? { metadataLanguage: "de" } : {}, path === "/api/settings" ? 200 : 500));
    const data = await settingsMetadataLoader();
    expect(data.settings).toMatchObject({ metadataLanguage: "de" });
    expect(data.metadataStatus).toBeNull();
  });
});

describe("Saved keys, every key and every failure", () => {
  const allUnreadable = () => {
    loaderData.settings = { ...emptyPlatformSettingsSnapshot, metadataLanguage: "en", metadataCertificationCountry: "AU", metadataTmdbApiKeyUnreadable: true, metadataOmdbApiKeyUnreadable: true, mdbListApiKeyUnreadable: true, metadataKeyProblem: "Saved on another computer." };
  };

  it("asks for each unreadable key and sends all three, trimmed, then clears the boxes", async () => {
    allUnreadable();
    renderPage();
    fireEvent.change(screen.getByLabelText("TMDb API key"), { target: { value: " tmdb-1 " } });
    fireEvent.change(screen.getByLabelText("OMDb API key"), { target: { value: "omdb-1" } });
    fireEvent.change(screen.getByLabelText("MDbList API key"), { target: { value: "mdb-1" } });
    fireEvent.click(screen.getByRole("button", { name: "Save keys" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalled());
    expect(JSON.parse((authedFetchMock.mock.calls[0][1] as RequestInit).body as string)).toEqual({ metadataTmdbApiKey: "tmdb-1", metadataOmdbApiKey: "omdb-1", mdbListApiKey: "mdb-1" });
    await waitFor(() => expect(screen.getByLabelText("TMDb API key")).toHaveValue(""));
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("sends just the MDbList key when only that one is typed", async () => {
    allUnreadable();
    renderPage();
    fireEvent.change(screen.getByLabelText("MDbList API key"), { target: { value: "mdb-2" } });
    fireEvent.click(screen.getByRole("button", { name: "Save keys" }));
    await waitFor(() => expect(authedFetchMock).toHaveBeenCalled());
    expect(JSON.parse((authedFetchMock.mock.calls[0][1] as RequestInit).body as string)).toEqual({ mdbListApiKey: "mdb-2" });
  });

  it("says why when the server refuses the key, and keeps what was typed", async () => {
    allUnreadable();
    authedFetchMock.mockResolvedValue(respond({ title: "That key was refused." }, 400));
    renderPage();
    fireEvent.change(screen.getByLabelText("TMDb API key"), { target: { value: "bad" } });
    fireEvent.click(screen.getByRole("button", { name: "Save keys" }));
    expect(await screen.findByText("That key was refused.")).toBeInTheDocument();
    expect(screen.getByLabelText("TMDb API key")).toHaveValue("bad");
  });

  it("uses plain words when the key save fails with something that has no message", async () => {
    allUnreadable();
    authedFetchMock.mockRejectedValue("gone");
    renderPage();
    fireEvent.change(screen.getByLabelText("TMDb API key"), { target: { value: "k" } });
    fireEvent.click(screen.getByRole("button", { name: "Save keys" }));
    expect(await screen.findByText("Could not save the key")).toBeInTheDocument();
  });
});

describe("Save metadata settings, odd cases", () => {
  it("uses plain words when the save fails with something that has no message", async () => {
    authedFetchMock.mockRejectedValue("gone");
    renderPage();
    fireEvent.change(screen.getByLabelText("Details language"), { target: { value: "de" } });
    fireEvent.click(screen.getByRole("button", { name: "Save metadata settings" }));
    expect(await screen.findByText("Could not save")).toBeInTheDocument();
  });

  it("sends one save when the form is submitted again while the first is still going", async () => {
    let finish: (value: unknown) => void = () => undefined;
    authedFetchMock.mockImplementation(() => new Promise((resolve) => { finish = resolve; }));
    const view = renderPage();
    fireEvent.change(screen.getByLabelText("Details language"), { target: { value: "de" } });
    const form = view.container.querySelector("form") as HTMLFormElement;
    fireEvent.submit(form);
    await screen.findByText("Saving…");
    fireEvent.submit(form);
    expect(authedFetchMock).toHaveBeenCalledTimes(1);
    finish(respond({ metadataLanguage: "de" }));
    await waitFor(() => expect(screen.getByText("Saved just now")).toBeInTheDocument());
  });
});
