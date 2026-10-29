import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

const { UsenetProviderPanel } = await import("./usenet-provider-panel");
const { isUsenetProtocol } = await import("../../lib/protocol");

describe("Usenet provider (#1142)", () => {
  afterEach(() => fetchJson.mockReset());

  it("says a Usenet client has no provider instead of leaving it looking healthy", async () => {
    fetchJson.mockResolvedValueOnce({ clients: [{ clientId: "c1", clientName: "SABnzbd", hasProvider: false, known: true }] });

    render(<UsenetProviderPanel clientId="c1" />);

    expect(await screen.findByText("No Usenet provider set up")).toBeTruthy();
  });

  it("takes the provider once and sends it to Deluno to write to every Usenet client", async () => {
    fetchJson.mockResolvedValueOnce({ clients: [{ clientId: "c1", clientName: "SABnzbd", hasProvider: false, known: true }] });
    fetchJson.mockResolvedValueOnce({ saved: true, clients: [{ clientId: "c1", clientName: "SABnzbd", saved: true, message: "SABnzbd now uses news.example.com." }] });
    fetchJson.mockResolvedValueOnce({ clients: [{ clientId: "c1", clientName: "SABnzbd", hasProvider: true, known: true }] });

    render(<UsenetProviderPanel clientId="c1" />);
    await screen.findByText("No Usenet provider set up");
    fireEvent.change(screen.getByLabelText("News server address"), { target: { value: "news.example.com" } });
    fireEvent.change(screen.getByLabelText(/Provider password/), { target: { value: "secret" } });
    fireEvent.click(screen.getByRole("button", { name: "Save provider to my Usenet clients" }));

    expect(await screen.findByText("SABnzbd now uses news.example.com.")).toBeTruthy();
    const [, init] = fetchJson.mock.calls[1];
    expect(fetchJson.mock.calls[1][0]).toBe("/api/download-clients/usenet-provider");
    expect(JSON.parse(init.body)).toMatchObject({ host: "news.example.com", port: 563, useSsl: true, password: "secret", connections: 8 });
    expect(await screen.findByText("Provider set up")).toBeTruthy();
    expect((screen.getByLabelText(/Provider password/) as HTMLInputElement).value).toBe("");
  });

  it("says Provider set up when the client has one, and Could not check when the client cannot say", async () => {
    fetchJson.mockResolvedValueOnce({ clients: [{ clientId: "c1", clientName: "SABnzbd", hasProvider: true, known: true }] });
    const first = render(<UsenetProviderPanel clientId="c1" />);
    expect(await screen.findByText("Provider set up")).toBeTruthy();
    first.unmount();

    fetchJson.mockResolvedValueOnce({ clients: [{ clientId: "c2", clientName: "NZBGet", hasProvider: false, known: false }] });
    render(<UsenetProviderPanel clientId="c2" />);
    expect(await screen.findByText("Could not check")).toBeTruthy();
  });

  it("shows no state chip when the clients cannot be read, and keeps Save off until an address is typed", async () => {
    fetchJson.mockRejectedValueOnce(new Error("offline"));
    render(<UsenetProviderPanel />);
    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(1));
    expect(screen.queryByText("Provider set up")).toBeNull();
    expect(screen.queryByText("Could not check")).toBeNull();
    expect((screen.getByRole("button", { name: "Save provider to my Usenet clients" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("falls back to the first client when the one it was opened for is not listed", async () => {
    fetchJson.mockResolvedValueOnce({ clients: [{ clientId: "c9", clientName: "NZBGet", hasProvider: true, known: true }] });
    render(<UsenetProviderPanel clientId="other" />);
    expect(await screen.findByText("Provider set up")).toBeTruthy();
  });

  it("sends a blank username and password as nothing, tells the page it saved, and marks a client that failed", async () => {
    const onSaved = vi.fn();
    fetchJson.mockResolvedValueOnce({ clients: [] });
    fetchJson.mockResolvedValueOnce({ saved: false, clients: [
      { clientId: "c1", clientName: "SABnzbd", saved: true, message: "SABnzbd now uses news.example.com." },
      { clientId: "c2", clientName: "NZBGet", saved: false, message: "NZBGet could not be reached." }
    ] });
    fetchJson.mockResolvedValueOnce({ clients: [] });
    render(<UsenetProviderPanel onSaved={onSaved} />);
    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(1));
    fireEvent.change(screen.getByLabelText("News server address"), { target: { value: "  news.example.com  " } });
    fireEvent.change(screen.getByLabelText("News server port"), { target: { value: "119" } });
    fireEvent.change(screen.getByLabelText("Connections"), { target: { value: "20" } });
    fireEvent.click(screen.getByRole("switch", { name: /Secure connection/ }));
    fireEvent.click(screen.getByRole("button", { name: "Save provider to my Usenet clients" }));

    const bad = await screen.findByText("NZBGet could not be reached.");
    expect(bad.className).toContain("text-destructive");
    expect(screen.getByText("SABnzbd now uses news.example.com.").className).toContain("text-foreground");
    expect(JSON.parse(fetchJson.mock.calls[1][1].body)).toEqual({ host: "news.example.com", port: 119, useSsl: false, username: null, password: null, connections: 20 });
    expect(onSaved).toHaveBeenCalledTimes(1);
  });

  it("says why the save failed in the words the server gave, or in its own when the failure has none", async () => {
    fetchJson.mockResolvedValueOnce({ clients: [] });
    fetchJson.mockRejectedValueOnce(new Error("The news server refused the login."));
    fetchJson.mockRejectedValueOnce("nope");
    render(<UsenetProviderPanel />);
    await waitFor(() => expect(fetchJson).toHaveBeenCalledTimes(1));
    fireEvent.change(screen.getByLabelText("News server address"), { target: { value: "news.example.com" } });
    fireEvent.click(screen.getByRole("button", { name: "Save provider to my Usenet clients" }));
    expect((await screen.findByRole("alert")).textContent).toBe("The news server refused the login.");
    fireEvent.click(screen.getByRole("button", { name: "Save provider to my Usenet clients" }));
    await waitFor(() => expect(screen.getByRole("alert").textContent).toBe("Deluno could not save the provider."));
  });

  it("only treats SABnzbd and NZBGet as Usenet clients", () => {
    expect(isUsenetProtocol("sabnzbd")).toBe(true);
    expect(isUsenetProtocol("nzbget")).toBe(true);
    expect(isUsenetProtocol("qbittorrent")).toBe(false);
  });
});
