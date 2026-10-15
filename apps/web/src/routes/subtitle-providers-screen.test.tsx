import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../lib/use-auth";

/**
 * Find & Download > Subtitle providers: the list of every provider the build ships, the On switch, the drawer with only the
 * account fields a provider needs, Save, and Test with its plain-words result.
 */

vi.setConfig({ testTimeout: 30_000 });
vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
const toastSuccess = vi.hoisted(() => vi.fn());
const toastError = vi.hoisted(() => vi.fn());
vi.mock("sonner", () => ({ toast: { success: toastSuccess, error: toastError } }));
const revalidate = vi.fn();
const loaderData = { providers: [] as unknown[] };
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loaderData,
  useRevalidator: () => ({ revalidate })
}));

const { SubtitleProvidersPage } = await import("./subtitle-providers-screen");

const option = (over: Record<string, unknown>) => ({
  key: "p", displayName: "Provider", description: "A place subtitles come from.", scope: "both", needsUsername: false, needsPassword: false, needsApiKey: false,
  credentialsOptional: false, configured: null, ...over
});
const configured = (over: Record<string, unknown> = {}) => ({
  id: "c1", providerKey: "opensubtitles", name: "OpenSubtitles", username: "me", hasSecret: true, hasApiKey: true, priority: 40, isEnabled: true, healthStatus: "healthy",
  lastHealthMessage: "Answered.", lastHealthLatencyMs: 12, lastHealthTestUtc: new Date().toISOString(), consecutiveFailures: 0, rateLimitedUntilUtc: null, ...over
});
const catalogue = () => [
  option({ key: "addic7ed", displayName: "Addic7ed", scope: "tv" }),
  option({ key: "opensubtitles", displayName: "OpenSubtitles", scope: "both", needsUsername: true, needsPassword: true, needsApiKey: true, configured: configured() }),
  option({ key: "subdl", displayName: "SubDL", scope: "movies", needsApiKey: true }),
  option({ key: "podnapisi", displayName: "Podnapisi", scope: "both", credentialsOptional: true, needsUsername: true, needsPassword: true })
];
const renderPage = () => render(<MemoryRouter><SubtitleProvidersPage /></MemoryRouter>);
const rowOf = (name: string) => screen.getByText(name).closest("[role='row']") as HTMLElement;
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

beforeEach(() => {
  cleanup();
  loaderData.providers = catalogue();
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockResolvedValue(json({}));
  toastSuccess.mockReset();
  toastError.mockReset();
  revalidate.mockReset();
});

describe("the list", () => {
  it("says how many are set up out of how many ship, with each provider's coverage, account need and last result", () => {
    renderPage();
    expect(screen.getByText("1 of 4 set up")).toBeInTheDocument();
    expect(within(rowOf("Addic7ed")).getByText("TV only")).toBeInTheDocument();
    expect(within(rowOf("Addic7ed")).getByText("None needed")).toBeInTheDocument();
    expect(within(rowOf("Addic7ed")).getByText("Not set up")).toBeInTheDocument();
    expect(within(rowOf("OpenSubtitles")).getByText("Required")).toBeInTheDocument();
    expect(within(rowOf("OpenSubtitles")).getByText("Saved: username, password, key")).toBeInTheDocument();
    expect(within(rowOf("OpenSubtitles")).getByText("Priority 40")).toBeInTheDocument();
    expect(within(rowOf("OpenSubtitles")).getByText("Working")).toBeInTheDocument();
    expect(within(rowOf("Podnapisi")).getByText("Optional")).toBeInTheDocument();
    expect(within(rowOf("SubDL")).getByText("Films only")).toBeInTheDocument();
  });

  it("reads a provider that is asking to be left alone as rate limited and one that answers with nothing as no results, neither as unreachable", () => {
    loaderData.providers = [
      option({ key: "a", displayName: "Limited", configured: configured({ healthStatus: "rate-limited" }) }),
      option({ key: "b", displayName: "Empty", configured: configured({ healthStatus: "degraded" }) }),
      option({ key: "c", displayName: "Down", configured: configured({ healthStatus: "failed" }) }),
      option({ key: "d", displayName: "Paused", configured: configured({ isEnabled: false }) })
    ];
    renderPage();
    expect(within(rowOf("Limited")).getByText("Rate limited")).toBeInTheDocument();
    expect(within(rowOf("Empty")).getByText("Answering, no results")).toBeInTheDocument();
    expect(within(rowOf("Down")).getByText("Not reachable")).toBeInTheDocument();
    expect(within(rowOf("Paused")).getByText("Off")).toBeInTheDocument();
  });
});

describe("the On switch", () => {
  it("is dimmed for a provider that needs an account nobody has set up, and works for one that needs none", async () => {
    renderPage();
    expect(screen.getByRole("switch", { name: "Enable SubDL" })).toBeDisabled();
    fireEvent.click(screen.getByRole("switch", { name: "Enable Addic7ed" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalled());
    const [url, init] = vi.mocked(authedFetch).mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/subtitle-providers/addic7ed");
    expect(JSON.parse(String(init.body))).toEqual({ providerKey: "addic7ed", username: null, priority: 100, isEnabled: true });
    await waitFor(() => expect(revalidate).toHaveBeenCalled());
  });

  it("sends the saved username and priority along when a configured provider is paused, never a secret", async () => {
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Pause OpenSubtitles" }));
    await waitFor(() => expect(authedFetch).toHaveBeenCalled());
    expect(JSON.parse(String((vi.mocked(authedFetch).mock.calls[0][1] as RequestInit).body))).toEqual({ providerKey: "opensubtitles", username: "me", priority: 40, isEnabled: false });
  });
});

describe("a provider's drawer", () => {
  it("asks only for the account fields the provider needs, and says a saved password and key are kept when left blank", () => {
    renderPage();
    fireEvent.click(screen.getByText("OpenSubtitles"));
    expect(screen.getByLabelText("Username")).toBeInTheDocument();
    expect(screen.getByLabelText("Password")).toBeInTheDocument();
    expect(screen.getByLabelText("API key")).toBeInTheDocument();
    expect(screen.getByText("A password is saved. Leave this blank to keep it.")).toBeInTheDocument();
    expect(screen.getByText("A key is saved. Leave this blank to keep it.")).toBeInTheDocument();
    expect(screen.getByLabelText("Password")).toHaveValue("");
    cleanup();
    renderPage();
    fireEvent.click(screen.getByText("Addic7ed"));
    expect(screen.getByText(/needs no account at all/)).toBeInTheDocument();
    expect(screen.queryByLabelText("Username")).toBeNull();
    cleanup();
    renderPage();
    fireEvent.click(screen.getByText("SubDL"));
    expect(screen.getByLabelText("API key")).toBeInTheDocument();
    expect(screen.queryByLabelText("Password")).toBeNull();
  });

  it("saves with a blank secret kept blank, toasts '<name> saved' and reads the list again; a field error shows under its field", async () => {
    renderPage();
    fireEvent.click(screen.getByText("OpenSubtitles"));
    fireEvent.change(screen.getByLabelText("Username"), { target: { value: "someone" } });
    fireEvent.click(screen.getByRole("button", { name: /^Save$/ }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("OpenSubtitles saved"));
    const call = vi.mocked(authedFetch).mock.calls.find(([url, init]) => url === "/api/subtitle-providers/opensubtitles" && (init as RequestInit).method === "PUT")!;
    expect(JSON.parse(String((call[1] as RequestInit).body))).toMatchObject({ providerKey: "opensubtitles", username: "someone", secret: "", apiKey: "", priority: 40, isEnabled: true });
    expect(revalidate).toHaveBeenCalled();

    vi.mocked(authedFetch).mockResolvedValue(json({ errors: { username: ["Enter the account name."] } }, 400));
    fireEvent.click(screen.getByText("OpenSubtitles"));
    fireEvent.click(screen.getByRole("button", { name: /^Save$/ }));
    expect(await screen.findByText("Enter the account name.")).toBeInTheDocument();
  });

  it("tests from the footer and says the result, adding what to do next when it failed; a reply it cannot read is explained, not 'could not run'", async () => {
    renderPage();
    fireEvent.click(screen.getByText("OpenSubtitles"));
    vi.mocked(authedFetch).mockResolvedValueOnce(json({ ok: true, message: "OpenSubtitles answered." }));
    fireEvent.click(screen.getByRole("button", { name: /Test/ }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("OpenSubtitles answered."));

    vi.mocked(authedFetch).mockResolvedValueOnce(json({ ok: false, message: "Login refused.", failure: { message: "Login refused.", nextAction: "Check the password." } }));
    fireEvent.click(screen.getByRole("button", { name: /Test/ }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Login refused. Check the password."));

    vi.mocked(authedFetch).mockResolvedValueOnce(new Response("<html>nope</html>", { status: 502 }));
    fireEvent.click(screen.getByRole("button", { name: /Test/ }));
    await waitFor(() => expect(toastError).toHaveBeenCalledTimes(2));
    expect(String(toastError.mock.calls[1][0])).not.toBe("The test could not be run.");
  });
});
