import { fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", () => ({ fetchJson }));

const { UsenetProviderPanel, isUsenetProtocol } = await import("./usenet-provider-panel");
const { ClientLoginReveal, isOnThisComputer } = await import("./client-login-reveal");

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

  it("only treats SABnzbd and NZBGet as Usenet clients", () => {
    expect(isUsenetProtocol("sabnzbd")).toBe(true);
    expect(isUsenetProtocol("nzbget")).toBe(true);
    expect(isUsenetProtocol("qbittorrent")).toBe(false);
  });
});

describe("a client's login on this computer (#1142)", () => {
  afterEach(() => fetchJson.mockReset());

  it("shows nothing until asked, then the address and login, and can hide it again", async () => {
    fetchJson.mockResolvedValueOnce({ address: "http://localhost:6789/", username: "nzbget", secret: "pw-123", secretLabel: "Password" });

    render(<ClientLoginReveal clientId="c2" name="NZBGet" />);
    expect(screen.queryByText("pw-123")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Show the login for NZBGet" }));

    expect(await screen.findByText("pw-123")).toBeTruthy();
    expect(screen.getByText("http://localhost:6789/")).toBeTruthy();
    expect(screen.getByText("nzbget")).toBeTruthy();
    expect(fetchJson).toHaveBeenCalledWith("/api/download-clients/c2/reveal-login", { method: "POST" });

    fireEvent.click(screen.getByRole("button", { name: "Hide the login" }));
    expect(screen.queryByText("pw-123")).toBeNull();
  });

  it("only offers it for a client on this computer", () => {
    expect(isOnThisComputer("localhost")).toBe(true);
    expect(isOnThisComputer("127.0.0.1")).toBe(true);
    expect(isOnThisComputer("nas.local")).toBe(false);
  });
});
