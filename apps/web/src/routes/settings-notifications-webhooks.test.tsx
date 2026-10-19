import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { NotificationPreferences, NotificationWebhookDeliveryItem, NotificationWebhookItem } from "../lib/api";
import { emptyPlatformSettingsSnapshot } from "../lib/api/types/settings";
import { authedFetch } from "../lib/use-auth";

/**
 * The webhook (notification connection) half of Settings > Notifications: the status chip, the row switch, the
 * drawer's provider fields and URL rule, add, edit, test, delete, the Delivery master switch and the Replay button.
 * The preference switches are in settings-notifications-page.test.tsx.
 */

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../hooks/use-unsaved-changes", () => ({ useUnsavedChanges: () => undefined }));
const toastSuccess = vi.fn();
const toastError = vi.fn();
vi.mock("../components/shell/toaster", () => ({ toast: { success: (...a: unknown[]) => toastSuccess(...a), error: (...a: unknown[]) => toastError(...a) } }));

const preferences: NotificationPreferences = {
  importCompletedEnabled: true, importFailedEnabled: true, providerHealthChangedEnabled: true, upgradeEnabled: false, renameEnabled: false,
  fileDeletedEnabled: false, manualInteractionRequiredEnabled: false, grabEnabled: false, addedEnabled: true, updateEnabled: false,
  stalledDownloadHandledEnabled: false, queueItemRemovedEnabled: false, orphanedDownloadRemovedEnabled: true, forcedImportEnabled: false,
  strikesPurgedEnabled: false, cleanupClearedEnabled: false, unmatchedImportGivenUpEnabled: false
};

const hook = (over: Partial<NotificationWebhookItem>): NotificationWebhookItem => ({
  id: "h1", name: "Family chat", url: "https://hooks.example.test/a", eventFilters: "grab,import", isEnabled: true, provider: "generic",
  configJson: null, tags: "", includeHealthWarnings: true, lastFiredUtc: null, lastError: null, createdUtc: "2026-10-01T00:00:00Z", updatedUtc: "2026-10-01T00:00:00Z",
  ...over
} as NotificationWebhookItem);

const loader = { settings: { ...emptyPlatformSettingsSnapshot, enableNotifications: true }, webhooks: [] as NotificationWebhookItem[], deliveries: [] as NotificationWebhookDeliveryItem[], preferences };
const revalidate = vi.fn();
vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => loader,
  useRevalidator: () => ({ revalidate })
}));

const { SettingsNotificationsPage } = await import("./settings-notifications-page");
const renderPage = () => render(<MemoryRouter><SettingsNotificationsPage /></MemoryRouter>);
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
const putCalls = () => vi.mocked(authedFetch).mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === "PUT");

beforeEach(() => {
  loader.webhooks = [];
  loader.deliveries = [];
  loader.settings = { ...emptyPlatformSettingsSnapshot, enableNotifications: true };
  vi.mocked(authedFetch).mockReset();
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input).startsWith("/api/tags") ? json([]) : json({ id: "h9" }));
  toastSuccess.mockReset();
  toastError.mockReset();
});
afterEach(() => localStorage.clear());

describe("webhook status chip", () => {
  it("says Off for a paused webhook, Paused when the master switch is off, Failing with a last error, Delivering once it has fired and Untested before", () => {
    loader.webhooks = [
      hook({ id: "a", name: "Off one", isEnabled: false }),
      hook({ id: "b", name: "Failing one", lastError: "HTTP 500" }),
      hook({ id: "c", name: "Delivering one", lastFiredUtc: "2026-10-02T00:00:00Z" }),
      hook({ id: "d", name: "Untested one" })
    ];
    const first = renderPage();
    const rowOf = (name: string) => screen.getByText(name).closest("[role='row']") as HTMLElement;
    expect(within(rowOf("Off one")).getByText("Off")).toBeInTheDocument();
    expect(within(rowOf("Failing one")).getByText("Failing")).toBeInTheDocument();
    expect(within(rowOf("Delivering one")).getByText("Delivering")).toBeInTheDocument();
    expect(within(rowOf("Untested one")).getByText("Untested")).toBeInTheDocument();
    first.unmount();

    loader.settings = { ...emptyPlatformSettingsSnapshot, enableNotifications: false };
    renderPage();
    expect(within(screen.getByText("Untested one").closest("[role='row']") as HTMLElement).getByText("Paused")).toBeInTheDocument();
    // A webhook that is itself switched off stays Off whatever the master switch says.
    expect(within(screen.getByText("Off one").closest("[role='row']") as HTMLElement).getByText("Off")).toBeInTheDocument();
  });
});

describe("webhook row switch", () => {
  it("PUTs the webhook with only isEnabled flipped, keeping its provider, config, filters and health-warning flag", async () => {
    loader.webhooks = [hook({ id: "t1", name: "Phone", provider: "telegram", url: "", configJson: "{\"botToken\":\"b\",\"chatId\":\"c\"}", eventFilters: "grab,health", includeHealthWarnings: false })];
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Pause Phone" }));
    await waitFor(() => expect(putCalls()).toHaveLength(1));
    const [url, init] = putCalls()[0];
    expect(url).toBe("/api/notification-webhooks/t1");
    expect(JSON.parse((init as RequestInit).body as string)).toEqual({
      name: "Phone", url: "", eventFilters: "grab,health", isEnabled: false, provider: "telegram", configJson: "{\"botToken\":\"b\",\"chatId\":\"c\"}", includeHealthWarnings: false
    });
    expect(revalidate).toHaveBeenCalled();
  });

  it("toasts the failure when the server refuses and says which webhook", async () => {
    loader.webhooks = [hook({ id: "t2", name: "Desk" })];
    vi.mocked(authedFetch).mockResolvedValue(json({}, 500));
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Pause Desk" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not pause Desk."));
  });
});

describe("webhook drawer", () => {
  const openNew = () => fireEvent.click(screen.getAllByRole("button", { name: "New webhook" })[0]);
  const chooseProvider = (value: string) => fireEvent.change(screen.getByLabelText("Provider"), { target: { value } });

  it("asks for the address a URL provider posts to, with that provider's own label, and sends nothing while it is blank", async () => {
    renderPage();
    openNew();
    chooseProvider("ntfy");
    expect(screen.getByText("Topic URL")).toBeInTheDocument();
    chooseProvider("gotify");
    expect(screen.getByText("Server URL")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Add webhook" }));
    expect(await screen.findByText("Paste the URL Deluno should POST to.")).toBeInTheDocument();
    expect(vi.mocked(authedFetch).mock.calls.filter(([url]) => url === "/api/notification-webhooks")).toHaveLength(0);
  });

  it("shows each provider's own fields: Telegram bot token and chat ID, Pushover app token and user key, Gotify and ntfy tokens", () => {
    renderPage();
    openNew();
    chooseProvider("telegram");
    expect(screen.getByText("Bot token")).toBeInTheDocument();
    expect(screen.getByText("Chat ID")).toBeInTheDocument();
    expect(screen.queryByPlaceholderText(/https?:\/\//i)).toBeNull();
    chooseProvider("pushover");
    expect(screen.getByText("App token")).toBeInTheDocument();
    expect(screen.getByText("User key")).toBeInTheDocument();
    chooseProvider("gotify");
    expect(screen.getByText("App token")).toBeInTheDocument();
    expect(screen.queryByText("User key")).toBeNull();
    chooseProvider("ntfy");
    expect(screen.getByText("Access token")).toBeInTheDocument();
  });

  it("shows the health-warnings switch only while Everything or Health problems is ticked, and sends it as includeHealthWarnings", async () => {
    renderPage();
    openNew();
    expect(screen.getByRole("switch", { name: /Include health warnings/ })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("checkbox", { name: /^Everything/ }));
    // Untick Everything: the plain set includes Health problems, so the switch stays; untick Health problems and it goes.
    fireEvent.click(screen.getByRole("checkbox", { name: /^Health problems/ }));
    expect(screen.queryByRole("switch", { name: /Include health warnings/ })).toBeNull();
    fireEvent.click(screen.getByRole("checkbox", { name: /^Health problems/ }));
    fireEvent.click(screen.getByRole("switch", { name: /Include health warnings/ }));
    fireEvent.change(screen.getByPlaceholderText(/https?:\/\//i), { target: { value: "https://hooks.example.test/x" } });
    fireEvent.click(screen.getByRole("button", { name: "Add webhook" }));
    await waitFor(() => expect(vi.mocked(authedFetch).mock.calls.some(([url, init]) => url === "/api/notification-webhooks" && (init as RequestInit).method === "POST")).toBe(true));
    const [, init] = vi.mocked(authedFetch).mock.calls.find(([url, o]) => url === "/api/notification-webhooks" && (o as RequestInit).method === "POST")!;
    expect(JSON.parse((init as RequestInit).body as string).includeHealthWarnings).toBe(true);
  });

  it("adds a webhook with its events, tags, config and provider, becomes the edit drawer and says Webhook added", async () => {
    renderPage();
    openNew();
    chooseProvider("telegram");
    fireEvent.change(screen.getByLabelText("Bot token"), { target: { value: "bot-1" } });
    fireEvent.change(screen.getByLabelText("Chat ID"), { target: { value: "chat-9" } });
    fireEvent.click(screen.getByRole("button", { name: "Add webhook" }));
    await waitFor(() => expect(screen.getByText("Webhook added")).toBeInTheDocument());
    const [, init] = vi.mocked(authedFetch).mock.calls.find(([url, o]) => url === "/api/notification-webhooks" && (o as RequestInit).method === "POST")!;
    const body = JSON.parse((init as RequestInit).body as string);
    expect(body).toMatchObject({ provider: "telegram", eventFilters: "all", isEnabled: true, tags: "" });
    expect(JSON.parse(body.configJson)).toEqual({ botToken: "bot-1", chatId: "chat-9" });
    // The same drawer is now the edit drawer of the saved webhook.
    expect(screen.getByRole("button", { name: "Save webhook" })).toBeInTheDocument();
  });

  it("shows 'Webhook could not be added.' when the server refuses an add", async () => {
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input).startsWith("/api/tags") ? json([]) : json({}, 400));
    renderPage();
    openNew();
    fireEvent.change(screen.getByPlaceholderText(/https?:\/\//i), { target: { value: "https://hooks.example.test/x" } });
    fireEvent.click(screen.getByRole("button", { name: "Add webhook" }));
    expect(await screen.findByText("Webhook could not be added.")).toBeInTheDocument();
  });

  it("opens a row in the edit drawer without a name field, saves with PUT and says Saved just now, or 'Webhook could not be saved.' on refusal", async () => {
    loader.webhooks = [hook({ id: "e1", name: "Family chat" })];
    renderPage();
    fireEvent.click(screen.getByText("Family chat"));
    expect(screen.queryByLabelText("Name")).toBeNull();
    fireEvent.change(screen.getByPlaceholderText(/https?:\/\//i), { target: { value: "https://hooks.example.test/b" } });
    fireEvent.click(screen.getByRole("button", { name: "Save webhook" }));
    await waitFor(() => expect(screen.getByText("Saved just now")).toBeInTheDocument());
    expect(putCalls()[0][0]).toBe("/api/notification-webhooks/e1");
    expect(JSON.parse((putCalls()[0][1] as RequestInit).body as string).url).toBe("https://hooks.example.test/b");

    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) => String(input).startsWith("/api/tags") ? json([]) : json({}, 500));
    fireEvent.change(screen.getByPlaceholderText(/https?:\/\//i), { target: { value: "https://hooks.example.test/c" } });
    fireEvent.click(screen.getByRole("button", { name: "Save webhook" }));
    expect(await screen.findByText("Webhook could not be saved.")).toBeInTheDocument();
  });

  it("sends a test event only from a saved form: dimmed with 'Save your changes first.' while edited, then POSTs the test and toasts", async () => {
    loader.webhooks = [hook({ id: "e2", name: "Test target" })];
    renderPage();
    fireEvent.click(screen.getByText("Test target"));
    const send = screen.getByRole("button", { name: "Send a test event" });
    expect(send).toBeEnabled();
    fireEvent.change(screen.getByPlaceholderText(/https?:\/\//i), { target: { value: "https://hooks.example.test/changed" } });
    expect(send).toBeDisabled();
    expect(screen.getByText("Save your changes first.")).toBeInTheDocument();
    fireEvent.change(screen.getByPlaceholderText(/https?:\/\//i), { target: { value: "https://hooks.example.test/a" } });
    expect(send).toBeEnabled();
    fireEvent.click(send);
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Test event sent to Test target"));
    expect(vi.mocked(authedFetch)).toHaveBeenCalledWith("/api/notification-webhooks/e2/test", { method: "POST" });
  });

  it("deletes after a confirmation that names the webhook, toasts '<name> removed' and closes the drawer", async () => {
    loader.webhooks = [hook({ id: "e3", name: "Old hook" })];
    renderPage();
    fireEvent.click(screen.getByText("Old hook"));
    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    expect(await screen.findByText("Delete “Old hook”?")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Delete webhook" }));
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Old hook removed"));
    expect(vi.mocked(authedFetch)).toHaveBeenCalledWith("/api/notification-webhooks/e3", { method: "DELETE" });
    await waitFor(() => expect(screen.queryByRole("button", { name: "Save webhook" })).toBeNull());
  });
});

describe("Delivery master switch and replay", () => {
  it("shows Sending or Paused and PATCHes enableNotifications when the master switch is flipped", async () => {
    const first = renderPage();
    expect(screen.getByText("Sending")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("switch", { name: "Send notifications" }));
    await waitFor(() => expect(vi.mocked(authedFetch).mock.calls.some(([url, o]) => url === "/api/settings" && (o as RequestInit).method === "PATCH")).toBe(true));
    const [, init] = vi.mocked(authedFetch).mock.calls.find(([url, o]) => url === "/api/settings" && (o as RequestInit).method === "PATCH")!;
    expect(JSON.parse((init as RequestInit).body as string)).toEqual({ enableNotifications: false });
    first.unmount();
    loader.settings = { ...emptyPlatformSettingsSnapshot, enableNotifications: false };
    renderPage();
    expect(screen.getByText("Paused")).toBeInTheDocument();
  });

  it("offers Replay only on dead-letter and retrying deliveries and toasts 'Delivery replayed' or the server's error", async () => {
    const delivery = (id: string, status: string): NotificationWebhookDeliveryItem => ({
      id, webhookId: "h1", eventCategory: "import", title: `Event ${id}`, status, attemptCount: 2, maxAttempts: 5, nextAttemptUtc: null, lastAttemptUtc: null,
      lastStatusCode: 500, lastError: null, createdUtc: "2026-10-03T00:00:00Z", updatedUtc: "2026-10-03T00:00:00Z"
    } as NotificationWebhookDeliveryItem);
    loader.webhooks = [hook({ id: "h1", name: "Chat" })];
    loader.deliveries = [delivery("dead", "dead-letter"), delivery("retry", "retrying"), delivery("ok", "delivered")];
    renderPage();
    const replays = screen.getAllByRole("button", { name: /Replay/ });
    expect(replays).toHaveLength(2);
    vi.mocked(authedFetch).mockResolvedValueOnce(json({ sent: true }));
    fireEvent.click(replays[0]);
    await waitFor(() => expect(toastSuccess).toHaveBeenCalledWith("Delivery replayed"));
    expect(vi.mocked(authedFetch).mock.calls.some(([url, o]) => /\/deliveries\/[a-z]+\/replay$/.test(String(url)) && (o as RequestInit).method === "POST")).toBe(true);
    vi.mocked(authedFetch).mockResolvedValueOnce(json({ sent: false, error: "The webhook answered 500." }));
    fireEvent.click(screen.getAllByRole("button", { name: /Replay/ })[1]);
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("The webhook answered 500."));
  });
});


describe("closing the drawer and the test button", () => {
  it("asks before throwing away an edit: Cancel in the prompt leaves the drawer open and Discard closes it and keeps nothing", async () => {
    loader.webhooks = [hook({ id: "d1", name: "Family chat" })];
    renderPage();
    fireEvent.click(screen.getByText("Family chat"));
    fireEvent.change(screen.getByPlaceholderText(/https?:\/\//i), { target: { value: "https://hooks.example.test/edited" } });
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(await screen.findByText("Discard unsaved changes?")).toBeInTheDocument();

    const prompt = () => screen.getByText("Discard unsaved changes?").closest('[role="alertdialog"], [role="dialog"]') as HTMLElement;
    fireEvent.click(within(prompt()).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByText("Discard unsaved changes?")).toBeNull());
    expect(screen.getByRole("button", { name: "Save webhook" })).toBeInTheDocument();

    fireEvent.click(screen.getAllByRole("button", { name: "Cancel" })[0]);
    await screen.findByText("Discard unsaved changes?");
    fireEvent.click(within(prompt()).getByRole("button", { name: "Discard" }));
    await waitFor(() => expect(screen.queryByRole("button", { name: "Save webhook" })).toBeNull());
    expect(putCalls()).toHaveLength(0);
  });

  it("says why the test event was not sent when the endpoint answers 200 with sent false", async () => {
    loader.webhooks = [hook({ id: "e9", name: "Quiet one" })];
    vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL) =>
      String(input).startsWith("/api/tags") ? json([]) : String(input).endsWith("/test") ? json({ sent: false, message: "Notifications are paused. Turn on Send notifications to test this webhook." }) : json({}));
    renderPage();
    fireEvent.click(screen.getByText("Quiet one"));

    fireEvent.click(screen.getByRole("button", { name: "Send a test event" }));

    await waitFor(() => expect(toastError).toHaveBeenCalled());
    expect(toastSuccess).not.toHaveBeenCalledWith("Test event sent to Quiet one");
  });
});
describe("preference switches (the three lists of events)", () => {
  it("shows every event list's saved state and dims a switch while its save is running", async () => {
    let finish: (value: Response) => void = () => undefined;
    vi.mocked(authedFetch).mockImplementation((input: RequestInfo | URL) => String(input) === "/api/notification-preferences"
      ? new Promise<Response>((resolve) => { finish = resolve; })
      : Promise.resolve(json([])));
    renderPage();
    // Import completed is on, Grab is off, Orphaned download removed is on, Cleanup cleared is off: one from each list.
    expect(screen.getByRole("switch", { name: "Import completed" })).toBeChecked();
    expect(screen.getByRole("switch", { name: "Grab" })).not.toBeChecked();
    expect(screen.getByRole("switch", { name: "Orphaned download removed" })).toBeChecked();
    expect(screen.getByRole("switch", { name: "Cleanup cleared" })).not.toBeChecked();
    fireEvent.click(screen.getByRole("switch", { name: "Grab" }));
    await waitFor(() => expect(screen.getByRole("switch", { name: "Grab" })).toBeDisabled());
    // Only that switch waits; the others stay usable.
    expect(screen.getByRole("switch", { name: "Import completed" })).toBeEnabled();
    finish(json({}));
    await waitFor(() => expect(screen.getByRole("switch", { name: "Grab" })).toBeEnabled());
  });

  it("toasts 'Could not save this preference.' when the server refuses a switch and sends the whole object with only that key changed", async () => {
    vi.mocked(authedFetch).mockResolvedValue(json({}, 500));
    renderPage();
    fireEvent.click(screen.getByRole("switch", { name: "Cleanup cleared" }));
    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Could not save this preference."));
    const [, init] = vi.mocked(authedFetch).mock.calls.find(([url]) => url === "/api/notification-preferences")!;
    expect(JSON.parse((init as RequestInit).body as string)).toEqual({ ...preferences, cleanupClearedEnabled: true });
  });
});
