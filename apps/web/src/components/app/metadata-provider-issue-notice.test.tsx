import { render, screen } from "@testing-library/react";
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
