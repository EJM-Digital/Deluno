import { afterEach, describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";

const authedFetchMock = vi.fn();
const toastMock = vi.hoisted(() => ({ error: vi.fn(), success: vi.fn() }));
vi.mock("./use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));
vi.mock("../components/shell/toaster", () => ({ toast: toastMock }));

import { fileNameFromDisposition } from "./signed-download";
import { SignedApiLink } from "../components/app/signed-api-link";

function answer(init: { ok: boolean; status?: number; body?: string; disposition?: string }) {
  return Promise.resolve({
    ok: init.ok,
    status: init.status ?? (init.ok ? 200 : 500),
    blob: () => Promise.resolve(new Blob([init.body ?? "zip"], { type: "application/zip" })),
    text: () => Promise.resolve(init.body ?? ""),
    headers: new Headers(init.disposition ? { "content-disposition": init.disposition } : {})
  } as Response);
}

describe("fileNameFromDisposition", () => {
  it("reads a plain, a quoted and an encoded file name, and nothing when there is none", () => {
    expect(fileNameFromDisposition('attachment; filename=deluno-backup.zip')).toBe("deluno-backup.zip");
    expect(fileNameFromDisposition('attachment; filename="deluno backup.zip"; size=3')).toBe("deluno backup.zip");
    expect(fileNameFromDisposition("attachment; filename*=UTF-8''deluno%20backup.zip")).toBe("deluno backup.zip");
    expect(fileNameFromDisposition(null)).toBeNull();
    expect(fileNameFromDisposition("inline")).toBeNull();
  });
});

describe("SignedApiLink (#1331)", () => {
  afterEach(() => { authedFetchMock.mockReset(); toastMock.error.mockReset(); vi.restoreAllMocks(); });

  it("fetches the route with the sign-in and saves it under the server's file name", async () => {
    const urls: string[] = [];
    vi.stubGlobal("URL", Object.assign(URL, { createObjectURL: (blob: Blob) => { urls.push(blob.type); return "blob:saved"; }, revokeObjectURL: () => undefined }));
    const saved: string[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) { saved.push(`${this.download}|${this.href}`); });
    authedFetchMock.mockReturnValue(answer({ ok: true, disposition: 'attachment; filename="server-name.zip"' }));

    render(<SignedApiLink path="/api/backups/b1/download" mode="save" fallbackName="fallback.zip">Download</SignedApiLink>);
    fireEvent.click(screen.getByRole("link", { name: "Download" }));

    await waitFor(() => expect(saved).toEqual(["server-name.zip|blob:saved"]));
    expect(authedFetchMock).toHaveBeenCalledWith("/api/backups/b1/download");
  });

  it("falls back to the name it was given when the server offers none", async () => {
    vi.stubGlobal("URL", Object.assign(URL, { createObjectURL: () => "blob:saved", revokeObjectURL: () => undefined }));
    const saved: string[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) { saved.push(this.download); });
    authedFetchMock.mockReturnValue(answer({ ok: true }));
    render(<SignedApiLink path="/api/backups/b1/download" mode="save" fallbackName="fallback.zip">Download</SignedApiLink>);
    fireEvent.click(screen.getByRole("link", { name: "Download" }));
    await waitFor(() => expect(saved).toEqual(["fallback.zip"]));
  });

  it("opens a text export in a new tab instead of saving it", async () => {
    vi.stubGlobal("URL", Object.assign(URL, { createObjectURL: () => "blob:view", revokeObjectURL: () => undefined }));
    const open = vi.spyOn(window, "open").mockImplementation(() => null);
    authedFetchMock.mockReturnValue(answer({ ok: true, body: "metrics" }));
    render(<SignedApiLink path="/api/monitoring/export/prometheus" mode="open" fallbackName="p.txt">Open</SignedApiLink>);
    fireEvent.click(screen.getByRole("link", { name: "Open" }));
    await waitFor(() => expect(open).toHaveBeenCalledWith("blob:view", "_blank", "noopener"));
  });

  it("tells the person why when the server refuses, and saves nothing", async () => {
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => undefined);
    authedFetchMock.mockReturnValue(answer({ ok: false, status: 404, body: "That backup is gone." }));
    render(<SignedApiLink path="/api/backups/b1/download" mode="save" fallbackName="f.zip">Download</SignedApiLink>);
    fireEvent.click(screen.getByRole("link", { name: "Download" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith(expect.stringContaining("That backup is gone")));
    expect(click).not.toHaveBeenCalled();
  });

  it("tells the person when the request never completes", async () => {
    authedFetchMock.mockRejectedValue(new Error("Failed to fetch"));
    render(<SignedApiLink path="/api/backups/b1/download" mode="save" fallbackName="f.zip">Download</SignedApiLink>);
    fireEvent.click(screen.getByRole("link", { name: "Download" }));
    await waitFor(() => expect(toastMock.error).toHaveBeenCalledWith("Failed to fetch"));
  });
});
