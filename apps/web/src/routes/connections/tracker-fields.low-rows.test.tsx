import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { IndexerItem, TrackerDefinitionSummary } from "../../lib/api";
import { emptyIndexerForm, type IndexerForm } from "./forms";

// #1329: the built-in Tracker protocol of the indexer drawer: the picker, what picking fills in, the chosen site's header and its secrets.

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<object>()), fetchJson }));
vi.mock("../../components/app/signed-api-link", () => ({ SignedApiLink: ({ children }: { children: React.ReactNode }) => <span>{children}</span> }));

const { TrackerFields } = await import("./tracker-fields");
const { TrackerPicker } = await import("./tracker-picker");

const tracker = (over: Partial<TrackerDefinitionSummary> & { id: string; name: string }): TrackerDefinitionSummary => ({
  description: `${over.name} is a site.`, language: "en-US", privacy: "public", links: [`https://${over.id}.example/`], movies: true, tv: true, loginMethod: null, settings: [],
  mayNeedCloudflareHelp: false, unsupported: null, origin: "bundled", ...over
});

const PRIVATE = tracker({
  id: "members", name: "Members Only", privacy: "private", language: "de-DE", movies: true, tv: false, description: "A members-only film site.",
  links: ["https://members.example/", "https://members.mirror.example/", "https://members.third.example/"],
  settings: [
    { name: "sortby", type: "select", label: "Sort by", default: "created", options: [{ key: "created", value: "Created" }, { key: "seeders", value: "Seeders" }] },
    { name: "freeleech", type: "checkbox", label: "Free leech only", default: "false", options: [] },
    { name: "username", type: "text", label: "Username", default: null, options: [] },
    { name: "passkey", type: "password", label: "Passkey", default: null, options: [], secret: true }
  ] as never
});
const PUBLIC = tracker({ id: "openfilms", name: "Open Films", privacy: "public", movies: true, tv: true });
const SERIES = tracker({ id: "shows", name: "Show Site", privacy: "semi-private", movies: false, tv: true });

function Host({ editing = null, initial }: { editing?: IndexerItem | null; initial?: Partial<IndexerForm> }) {
  const [form, setForm] = useState<IndexerForm>({ ...emptyIndexerForm(), protocol: "tracker", ...initial });
  return (
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <TrackerFields form={form} setForm={setForm} editing={editing} errors={{}} clearError={() => undefined} />
      <output data-testid="form">{JSON.stringify(form)}</output>
    </QueryClientProvider>
  );
}
const formNow = () => JSON.parse(screen.getByTestId("form").textContent!) as IndexerForm;

afterEach(() => { cleanup(); fetchJson.mockReset(); });

describe("the Tracker protocol picker, low rows (#1329)", () => {
  it("shows a loading line while the catalogue loads, then the picker with the sites listed", async () => {
    let release!: (value: unknown) => void;
    fetchJson.mockImplementation(() => new Promise((resolve) => { release = resolve; }));
    render(<Host />);

    expect(screen.getByText("Loading the trackers Deluno knows…")).toBeTruthy();
    expect(screen.queryByRole("listbox", { name: "Trackers" })).toBeNull();

    release([PRIVATE, PUBLIC]);

    expect(await screen.findByRole("listbox", { name: "Trackers" })).toBeTruthy();
    expect(screen.queryByText("Loading the trackers Deluno knows…")).toBeNull();
  });

  it("says No tracker matches these words for a search that finds nothing", async () => {
    fetchJson.mockResolvedValue([PRIVATE, PUBLIC]);
    render(<Host />);
    fireEvent.change(await screen.findByLabelText("Search trackers"), { target: { value: "zzzz nothing" } });

    expect(screen.getByText("No tracker matches these words.")).toBeTruthy();
  });

  it("lists at most 200 sites and says how many there are in all", () => {
    const many = Array.from({ length: 250 }, (_, index) => tracker({ id: `site${String(index).padStart(3, "0")}`, name: `Site ${String(index).padStart(3, "0")}` }));
    render(<TrackerPicker trackers={many} onPick={() => undefined} />);

    expect(within(screen.getByRole("listbox", { name: "Trackers" })).getAllByRole("option")).toHaveLength(200);
    expect(screen.getByText(/^Showing 200 of 250\./)).toBeTruthy();
  });

  it("picking a site fills its first address, Films or Series from its flags, its defaults, and a strict sharing rule for a site that is not public", async () => {
    fetchJson.mockResolvedValue([PRIVATE, PUBLIC, SERIES]);
    render(<Host />);

    fireEvent.click(await screen.findByRole("option", { name: /Members Only/ }));
    const chosen = formNow();
    expect(chosen.definitionId).toBe("members");
    expect(chosen.baseUrl).toBe("https://members.example/");
    expect(chosen.scope).toBe("movies");
    expect(chosen.trackerSettings).toEqual({ sortby: "created", freeleech: "false" });
    expect(chosen.sharingRule).toBe("strict");
    cleanup();

    render(<Host />);
    fireEvent.click(await screen.findByRole("option", { name: /Show Site/ }));
    expect(formNow().scope).toBe("tv");
    cleanup();

    render(<Host />);
    fireEvent.click(await screen.findByRole("option", { name: /Open Films/ }));
    const open = formNow();
    expect(open.scope).toBe("both");
    expect(open.sharingRule).toBe("inherit");
  });

  it("the chosen site's header names it with its privacy, language and description, and Choose another is offered for a new indexer only", async () => {
    fetchJson.mockResolvedValue([PRIVATE, PUBLIC]);
    render(<Host initial={{ definitionId: "members", baseUrl: "https://members.example/" }} />);

    expect(await screen.findByText("Members Only")).toBeTruthy();
    expect(screen.getByText("Private")).toBeTruthy();
    expect(screen.getByText("German (Germany)")).toBeTruthy();
    expect(screen.getByText("A members-only film site.")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Choose another" }));
    expect(formNow().definitionId).toBe("");
    expect(await screen.findByRole("listbox", { name: "Trackers" })).toBeTruthy();
    cleanup();

    render(<Host initial={{ definitionId: "members" }} editing={{ id: "i1", definitionSecretsSet: [] } as unknown as IndexerItem} />);
    expect(await screen.findByText("Members Only")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Choose another" })).toBeNull();
  });

  it("a site with several addresses offers them in a select and keeps the one in use when it is not among them", async () => {
    fetchJson.mockResolvedValue([PRIVATE, PUBLIC]);
    render(<Host initial={{ definitionId: "members", baseUrl: "https://members.mirror.example/" }} />);

    const select = await screen.findByLabelText("Address") as HTMLSelectElement;
    expect([...select.options].map((option) => option.value)).toEqual(["https://members.example/", "https://members.mirror.example/", "https://members.third.example/"]);
    expect(select.value).toBe("https://members.mirror.example/");
    fireEvent.change(select, { target: { value: "https://members.third.example/" } });
    expect(formNow().baseUrl).toBe("https://members.third.example/");
    cleanup();

    render(<Host initial={{ definitionId: "openfilms", baseUrl: "" }} />);
    await screen.findByText("Open Films");
    expect(screen.queryByLabelText("Address")).toBeNull();
    cleanup();

    render(<Host initial={{ definitionId: "members", baseUrl: "https://moved.example/" }} />);
    const moved = await screen.findByLabelText("Address") as HTMLSelectElement;
    expect([...moved.options].map((option) => option.value)[0]).toBe("https://moved.example/");
  });

  it("a site's secret is masked, stored encrypted, says Saved and encrypted when one is held, and a blank one keeps the saved value", async () => {
    fetchJson.mockResolvedValue([PRIVATE]);
    render(<Host initial={{ definitionId: "members" }} editing={{ id: "i1", definitionSecretsSet: ["passkey"] } as unknown as IndexerItem} />);

    const passkey = await screen.findByLabelText("Passkey") as HTMLInputElement;
    expect(passkey.type).toBe("password");
    expect(passkey.placeholder).toBe("••••••••  (unchanged)");
    expect(screen.getByText("Saved and encrypted. Leave empty to keep it; type a new one to replace it.")).toBeTruthy();
    expect(passkey.value).toBe("");
    expect(formNow().trackerSecrets).toEqual({});
    fireEvent.change(passkey, { target: { value: "new-key" } });
    expect(formNow().trackerSecrets).toEqual({ passkey: "new-key" });
    cleanup();

    render(<Host initial={{ definitionId: "members" }} />);
    expect(await screen.findByText("Stored encrypted, and never shown again.")).toBeTruthy();
    expect((screen.getByLabelText("Passkey") as HTMLInputElement).type).toBe("password");
  });
});
