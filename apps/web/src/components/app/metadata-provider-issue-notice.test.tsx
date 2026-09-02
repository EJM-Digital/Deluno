import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../../lib/use-auth";
import type { MetadataProviderIssue } from "../../lib/api";
import { MetadataProviderIssueNotice } from "./metadata-provider-issue-notice";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../shell/toaster", () => ({
  toast: { success: vi.fn(), error: vi.fn() }
}));

const issue: MetadataProviderIssue = {
  kind: "provider-record-missing",
  provider: "tmdb",
  providerId: "1603343",
  evidenceKey: "tmdb:movie:1603343:missing",
  detectedUtc: "2026-09-01T00:00:00Z",
  acknowledgedUtc: null
};

describe("MetadataProviderIssueNotice", () => {
  beforeEach(() => vi.clearAllMocks());

  it("presents provider removal as a title decision, not an emergency", () => {
    render(
      <MetadataProviderIssueNotice
        issue={issue}
        subjectLabel="movie"
        acknowledgeUrl="/api/movies/movie-1/metadata/issue/acknowledge"
        onAcknowledged={vi.fn()}
        onFindAnother={vi.fn()}
        onRetry={vi.fn()}
      />
    );

    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
    const notice = screen.getByRole("region", { name: "This movie is no longer listed by TMDb" });
    expect(notice).toBeVisible();
    expect(notice.querySelector(".flex-col.sm\\:flex-row")).not.toBeNull();
    expect(screen.getByText(/kept the title, monitoring, history, and files/i)).toBeVisible();
    expect(screen.getByRole("button", { name: "Try again" })).toBeVisible();
    expect(screen.getByRole("button", { name: "Find another match" })).toBeVisible();
    expect(screen.getByRole("button", { name: "Keep this movie" })).toBeVisible();
    expect(screen.queryByRole("button", { name: /delete|remove/i })).not.toBeInTheDocument();
  });

  it("durably acknowledges the unchanged evidence without deleting the movie", async () => {
    const user = userEvent.setup();
    const onAcknowledged = vi.fn();
    vi.mocked(authedFetch).mockResolvedValue(new Response(null, { status: 204 }));

    render(
      <MetadataProviderIssueNotice
        issue={issue}
        subjectLabel="movie"
        acknowledgeUrl="/api/movies/movie-1/metadata/issue/acknowledge"
        onAcknowledged={onAcknowledged}
        onFindAnother={vi.fn()}
        onRetry={vi.fn()}
      />
    );

    screen.getByRole("button", { name: "Keep this movie" }).focus();
    await user.keyboard("{Enter}");

    expect(authedFetch).toHaveBeenCalledWith(
      "/api/movies/movie-1/metadata/issue/acknowledge",
      { method: "POST" }
    );
    expect(onAcknowledged).toHaveBeenCalledOnce();
  });

  it("keeps retry and remap as keyboard-reachable non-destructive choices", async () => {
    const user = userEvent.setup();
    const onRetry = vi.fn();
    const onFindAnother = vi.fn();

    render(
      <MetadataProviderIssueNotice
        issue={issue}
        subjectLabel="show"
        acknowledgeUrl="/api/series/show-1/metadata/issue/acknowledge"
        onAcknowledged={vi.fn()}
        onFindAnother={onFindAnother}
        onRetry={onRetry}
      />
    );

    await user.tab();
    expect(screen.getByRole("button", { name: "Try again" })).toHaveFocus();
    await user.keyboard("{Enter}");
    await user.tab();
    expect(screen.getByRole("button", { name: "Find another match" })).toHaveFocus();
    await user.keyboard("{Enter}");

    expect(onRetry).toHaveBeenCalledOnce();
    expect(onFindAnother).toHaveBeenCalledOnce();
  });

  it("reaches every choice by keyboard, in the order they are offered", async () => {
    const user = userEvent.setup();

    render(
      <MetadataProviderIssueNotice
        issue={issue}
        subjectLabel="movie"
        acknowledgeUrl="/api/movies/movie-1/metadata/issue/acknowledge"
        onAcknowledged={vi.fn()}
        onFindAnother={vi.fn()}
        onRetry={vi.fn()}
      />
    );

    // The whole resolution flow, not only the two non-destructive halves:
    // somebody using a keyboard has to be able to reach the choice that
    // dismisses the notice as well as the ones that act on it.
    await user.tab();
    expect(screen.getByRole("button", { name: "Try again" })).toHaveFocus();
    await user.tab();
    expect(screen.getByRole("button", { name: "Find another match" })).toHaveFocus();
    await user.tab();
    expect(screen.getByRole("button", { name: "Keep this movie" })).toHaveFocus();

    // And focus leaves again: a title-level notice must never trap it.
    await user.tab();
    expect(screen.getByRole("button", { name: "Keep this movie" })).not.toHaveFocus();
  });

  it("announces itself as a named region rather than an unlabelled block", () => {
    render(
      <MetadataProviderIssueNotice
        issue={issue}
        subjectLabel="movie"
        acknowledgeUrl="/api/movies/movie-1/metadata/issue/acknowledge"
        onAcknowledged={vi.fn()}
        onFindAnother={vi.fn()}
        onRetry={vi.fn()}
      />
    );

    // A screen reader reaches this by region, and the region has to say what
    // it is about before any of the three choices make sense.
    const region = screen.getByRole("region", {
      name: /no longer listed by TMDb/i
    });
    expect(region).toBeInTheDocument();

    // Every control has a name of its own; none of them is "button".
    for (const name of ["Try again", "Find another match", "Keep this movie"]) {
      expect(within(region).getByRole("button", { name })).toBeInTheDocument();
    }

    // The decorative icons must not be read out.
    expect(within(region).queryByRole("img")).toBeNull();
  });

  it("says out loud that it is working, instead of only spinning", async () => {
    const user = userEvent.setup();
    let release: (() => void) | undefined;
    vi.mocked(authedFetch).mockReturnValueOnce(
      new Promise((resolve) => {
        release = () => resolve(new Response(null, { status: 204 }));
      }) as ReturnType<typeof authedFetch>
    );

    render(
      <MetadataProviderIssueNotice
        issue={issue}
        subjectLabel="movie"
        acknowledgeUrl="/api/movies/movie-1/metadata/issue/acknowledge"
        onAcknowledged={vi.fn()}
        onFindAnother={vi.fn()}
        onRetry={vi.fn()}
      />
    );

    const keep = screen.getByRole("button", { name: "Keep this movie" });
    await user.click(keep);

    // A spinner is nothing at all to a screen reader: without this the button
    // simply went quiet and stopped responding.
    expect(keep).toHaveAttribute("aria-busy", "true");
    expect(screen.getByRole("status")).toHaveTextContent("Keeping this movie.");

    release?.();
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent(""));
  });

  it("stays out of the page after the same evidence has been acknowledged", () => {
    const { container } = render(
      <MetadataProviderIssueNotice
        issue={{ ...issue, acknowledgedUtc: "2026-09-01T00:05:00Z" }}
        subjectLabel="movie"
        acknowledgeUrl="/api/movies/movie-1/metadata/issue/acknowledge"
        onAcknowledged={vi.fn()}
        onFindAnother={vi.fn()}
        onRetry={vi.fn()}
      />
    );

    expect(container).toBeEmptyDOMElement();
  });
});
