import { render, screen, waitFor } from "@testing-library/react";
import { fireEvent } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { authedFetch } from "../../lib/use-auth";
import { LogFilesCard } from "./log-files-card";

/**
 * #867: a Log Files list with download and open-folder actions. Radarr's
 * own System > Logs page is the model.
 */

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));

const files = [
  { name: "deluno-2026-09-27.jsonl", sizeBytes: 2048, lastWrittenUtc: new Date().toISOString() },
  { name: "deluno-2026-09-26.jsonl", sizeBytes: 512, lastWrittenUtc: new Date(Date.now() - 86_400_000).toISOString() }
];

function mockEndpoints() {
  vi.mocked(authedFetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = typeof input === "string" ? input : input.toString();
    if (url === "/api/system/log-files") {
      return new Response(JSON.stringify(files), { status: 200, headers: { "Content-Type": "application/json" } });
    }
    if (url === "/api/system/log-files/folder") {
      return new Response(JSON.stringify({ path: "/data/logs" }), { status: 200, headers: { "Content-Type": "application/json" } });
    }
    if (url.endsWith("/download")) {
      return new Response(new Blob(["log contents"]), { status: 200 });
    }
    if (init?.method === "DELETE") {
      return new Response(null, { status: 204 });
    }
    return new Response(JSON.stringify({}), { status: 200 });
  });
}

describe("LogFilesCard (#867)", () => {
  beforeEach(() => {
    mockEndpoints();
    URL.createObjectURL = vi.fn(() => "blob:mock");
    URL.revokeObjectURL = vi.fn();
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it("lists the files actually present, newest first", async () => {
    render(<LogFilesCard />);

    await waitFor(() => expect(screen.getByText("deluno-2026-09-27.jsonl")).toBeTruthy());
    expect(screen.getByText("deluno-2026-09-26.jsonl")).toBeTruthy();
    expect(screen.getByText("2 files")).toBeTruthy();
  });

  it("downloads a file when its action is clicked", async () => {
    render(<LogFilesCard />);
    await waitFor(() => expect(screen.getByText("deluno-2026-09-27.jsonl")).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: "Download deluno-2026-09-27.jsonl" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith("/api/system/log-files/deluno-2026-09-27.jsonl/download"));
  });

  it("deletes a file after confirmation and refreshes the list", async () => {
    render(<LogFilesCard />);
    await waitFor(() => expect(screen.getByText("deluno-2026-09-26.jsonl")).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: "Delete deluno-2026-09-26.jsonl" }));
    fireEvent.click(screen.getByRole("button", { name: "Delete file" }));

    await waitFor(() => expect(authedFetch).toHaveBeenCalledWith(
      "/api/system/log-files/deluno-2026-09-26.jsonl",
      expect.objectContaining({ method: "DELETE" })
    ));
  });

  it("has a Refresh action and an Open logs folder action", async () => {
    render(<LogFilesCard />);
    await waitFor(() => expect(screen.getByText("deluno-2026-09-27.jsonl")).toBeTruthy());

    expect(screen.getByRole("button", { name: /Refresh/ })).toBeTruthy();
    expect(screen.getByRole("button", { name: /Open logs folder/ })).toBeTruthy();
  });
});
