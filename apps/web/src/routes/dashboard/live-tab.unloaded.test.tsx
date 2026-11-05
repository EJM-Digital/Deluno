import { QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../../lib/route-cache";
import { LiveTab } from "./live-tab";

// Nothing the Live tab reads has answered yet, and the summary is not there either: every list it holds is missing,
// and the tab draws its empty cards rather than failing. (These fall-backs used to be exercised only by accident, by
// route tests that rendered the whole Dashboard.)
vi.mock("../../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/use-signalr")>()),
  useSignalREvent: () => undefined
}));
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn(() => new Promise(() => undefined)) }));
vi.mock("../../lib/api", async (importOriginal) => {
  const original = await importOriginal<typeof import("../../lib/api")>();
  const never = () => new Promise<never>(() => undefined);
  return { ...original, fetchJson: never, fetchAllPages: never, fetchPageItems: never };
});

beforeEach(() => queryClient.clear());

describe("Live tab before anything has loaded", () => {
  it("draws its cards with no summary and no lists", () => {
    const { container } = render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <LiveTab summary={undefined} />
        </MemoryRouter>
      </QueryClientProvider>
    );
    expect(container.querySelector("[data-page-layout]")).not.toBeNull();
    expect(screen.getByRole("region", { name: "Download clients" })).toBeInTheDocument();
  });
});
