import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../../lib/use-auth";
import type { MetadataProviderIssue } from "../../lib/api";
import { MetadataProviderIssueNotice } from "./metadata-provider-issue-notice";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));
const { toast } = (await import("../shell/toaster")) as unknown as { toast: { success: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn> } };

function issue(overrides: Partial<MetadataProviderIssue> = {}): MetadataProviderIssue {
  return { kind: "provider-record-missing", provider: "tmdb", providerId: "1", evidenceKey: "k", detectedUtc: "2026-09-01T00:00:00Z", acknowledgedUtc: null, ...overrides };
}

function renderNotice(value: MetadataProviderIssue | null, subjectLabel: "movie" | "show" = "movie") {
  const handlers = { onAcknowledged: vi.fn(), onFindAnother: vi.fn(), onRetry: vi.fn() };
  const view = render(<MetadataProviderIssueNotice issue={value} subjectLabel={subjectLabel} acknowledgeUrl="/api/x/acknowledge" {...handlers} />);
  return { ...handlers, ...view };
}

describe("the metadata note on a title", () => {
  beforeEach(() => { vi.clearAllMocks(); });

  it("shows nothing without a note, or once it has been acknowledged", () => {
    expect(renderNotice(null).container).toBeEmptyDOMElement();
    expect(renderNotice(issue({ acknowledgedUtc: "2026-09-02T00:00:00Z" })).container).toBeEmptyDOMElement();
  });

  it("names a provider other than TMDb as it is written, and TMDb in capitals whatever its case", () => {
    const first = renderNotice(issue({ provider: "TheTVDB" }), "show");
    expect(screen.getByText("This show is no longer listed by TheTVDB")).toBeInTheDocument();
    first.unmount();
    renderNotice(issue({ provider: "TMDB" }));
    expect(screen.getByText("This movie is no longer listed by TMDb")).toBeInTheDocument();
  });

  it("says an unreadable episode list is about a service that did not answer, with no Find another match", () => {
    renderNotice(issue({ kind: "catalogue-unreadable" }), "show");
    expect(screen.getByText("Deluno could not read this show's episode list")).toBeInTheDocument();
    expect(screen.getByText(/did not answer, so the episodes Deluno already has were left as they are/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Find another match" })).toBeNull();
    expect(screen.getByRole("button", { name: "Clear this note" })).toBeInTheDocument();
  });

  it("says a show on TMDb's numbering for now is that, with no Find another match", () => {
    renderNotice(issue({ kind: "catalogue-tvdb-unavailable" }), "show");
    expect(screen.getByText("This show's episodes use TMDb's numbering for now")).toBeInTheDocument();
    expect(screen.getByText(/TheTVDB did not answer when this show was added/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Find another match" })).toBeNull();
    expect(screen.getByRole("button", { name: "Clear this note" })).toBeInTheDocument();
  });

  it("offers three answers for a title the provider dropped, and each does what it says", async () => {
    const { onFindAnother, onRetry } = renderNotice(issue());
    expect(screen.getByText(/Deluno kept the title, monitoring, history, and files/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Find another match" }));
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(onFindAnother).toHaveBeenCalledTimes(1);
    expect(onRetry).toHaveBeenCalledTimes(1);
    expect(screen.getByRole("button", { name: "Keep this movie" })).toBeInTheDocument();
  });

  it("keeps the title and says so when acknowledged, accepting an empty answer as well as an ordinary one", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: false, status: 204 } as Response);
    const { onAcknowledged } = renderNotice(issue());

    await userEvent.click(screen.getByRole("button", { name: "Keep this movie" }));

    expect(authedFetch).toHaveBeenCalledWith("/api/x/acknowledge", { method: "POST" });
    expect(toast.success).toHaveBeenCalledWith("Kept this movie. The metadata note was cleared.");
    expect(onAcknowledged).toHaveBeenCalledTimes(1);
  });

  it("says the note was cleared when it is about the episode list", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: true, status: 200 } as Response);
    const { onAcknowledged } = renderNotice(issue({ kind: "catalogue-unreadable" }), "show");

    await userEvent.click(screen.getByRole("button", { name: "Clear this note" }));

    expect(toast.success).toHaveBeenCalledWith("The note was cleared.");
    expect(onAcknowledged).toHaveBeenCalledTimes(1);
  });

  it("says the note could not be cleared when the server refuses, and lets the person try again", async () => {
    vi.mocked(authedFetch).mockResolvedValue({ ok: false, status: 500 } as Response);
    const { onAcknowledged } = renderNotice(issue());

    await userEvent.click(screen.getByRole("button", { name: "Keep this movie" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("That metadata note could not be cleared."));
    expect(toast.success).not.toHaveBeenCalled();
    expect(onAcknowledged).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Keep this movie" })).toBeEnabled();
  });

  it("says the note could not be cleared when the request itself fails", async () => {
    vi.mocked(authedFetch).mockRejectedValue(new Error("offline"));
    renderNotice(issue());

    await userEvent.click(screen.getByRole("button", { name: "Keep this movie" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("That metadata note could not be cleared."));
  });

  it("tells a screen reader what is happening while it clears, and the button cannot be pressed again", async () => {
    vi.mocked(authedFetch).mockReturnValue(new Promise(() => undefined));
    renderNotice(issue());

    await userEvent.click(screen.getByRole("button", { name: "Keep this movie" }));

    expect(screen.getByRole("status")).toHaveTextContent("Keeping this movie.");
    expect(screen.getByRole("button", { name: "Keep this movie" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Keep this movie" })).toHaveAttribute("aria-busy", "true");
  });

  it("tells a screen reader the note is being cleared when it is about the episode list, and says nothing before", async () => {
    vi.mocked(authedFetch).mockReturnValue(new Promise(() => undefined));
    renderNotice(issue({ kind: "catalogue-unreadable" }), "show");
    expect(screen.getByRole("status")).toHaveTextContent("");

    await userEvent.click(screen.getByRole("button", { name: "Clear this note" }));

    expect(screen.getByRole("status")).toHaveTextContent("Clearing the note.");
  });
});
