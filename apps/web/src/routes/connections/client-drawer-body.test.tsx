import { fireEvent, render, screen } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { afterEach, describe, expect, it } from "vitest";
import { ClientDrawerBody } from "./client-drawer-body";
import { emptyClientForm } from "./forms";
import type { ClientForm } from "./forms";

/**
 * #980: the rTorrent preset's own hint tells the person to set "/RPC2" as the
 * URL base "below", but the field used to render only for Usenet presets
 * (SABnzbd/NZBGet) — an rTorrent behind ruTorrent or a custom RPC path had no
 * way to set it from the drawer at all.
 */
function renderDrawer(form: ClientForm) {
  render(
    <ClientDrawerBody
      form={form}
      setForm={() => {}}
      editing={null}
      errors={{}}
      clearError={() => {}}
      mappings={[]}
      newMapping={{ remotePath: "", localPath: "" }}
      setNewMapping={() => {}}
      busy={null}
      onAddMapping={() => {}}
      onRemoveMapping={() => {}}
      onTestMapping={() => {}}
      onRemove={() => {}}
    />
  );
  // The URL base field sits inside the "Advanced" disclosure (title +
  // "Priority N" summary), closed by default — open it, the way a person
  // would, before looking for the field. A qBittorrent client's own "Save
  // path" field has an unrelated "Advanced" button of its own (PathInput),
  // so the match has to be specific to the disclosure's own accessible name.
  fireEvent.click(screen.getByRole("button", { name: /^Advanced.*Priority/ }));
}

describe("ClientDrawerBody URL base field (#980)", () => {
  it("shows for rTorrent", () => {
    renderDrawer({ ...emptyClientForm(), protocol: "rtorrent" });
    expect(screen.getByLabelText("URL base")).toBeTruthy();
  });

  it("still shows for a Usenet client", () => {
    renderDrawer({ ...emptyClientForm(), protocol: "sabnzbd" });
    expect(screen.getByLabelText("URL base")).toBeTruthy();
  });

  it("does not show for a torrent client with no path convention of its own", () => {
    renderDrawer({ ...emptyClientForm(), protocol: "qbittorrent" });
    expect(screen.queryByLabelText("URL base")).toBeNull();
  });

  it("pre-fills /RPC2 when a new client is switched to the rTorrent preset", () => {
    renderDrawer({ ...emptyClientForm(), protocol: "rtorrent" });
    // choosePreset only fires from the Client picker's onChange, but a new
    // client created directly with the rtorrent protocol (as the picker
    // would leave it) still has to show the field with its value editable.
    expect((screen.getByLabelText("URL base") as HTMLInputElement).placeholder).toBe("/RPC2");
  });
});

describe("ClientDrawerBody open the client's page (#1142)", () => {
  function renderEditing(form: ClientForm) {
    render(
      <QueryClientProvider client={new QueryClient()}><ClientDrawerBody
        form={form}
        setForm={() => {}}
        editing={{ id: "c1", name: "Seedbox" } as never}
        errors={{}}
        clearError={() => {}}
        mappings={[]}
        newMapping={{ remotePath: "", localPath: "" }}
        setNewMapping={() => {}}
        busy={null}
        onAddMapping={() => {}}
        onRemoveMapping={() => {}}
        onTestMapping={() => {}}
        onRemove={() => {}}
      /></QueryClientProvider>
    );
  }

  it("links to the client's own web page in a new tab and prints the address, never the password", () => {
    renderEditing({ ...emptyClientForm(), protocol: "qbittorrent", host: "192.168.1.10", port: "8080", useSsl: true, username: "me", password: "hunter2" });
    const link = screen.getByRole("link", { name: /^Open qBittorrent.s page$/ });
    expect(link.getAttribute("href")).toBe("https://192.168.1.10:8080/");
    expect(link.getAttribute("target")).toBe("_blank");
    expect(screen.getByText("https://192.168.1.10:8080/")).toBeTruthy();
    expect(link.getAttribute("href")).not.toContain("hunter2");
    expect(screen.getByText("https://192.168.1.10:8080/").textContent).not.toContain("hunter2");
  });

  it("includes the URL base where the client has one", () => {
    renderEditing({ ...emptyClientForm(), protocol: "sabnzbd", host: "nas.local", port: "8085", urlBase: "/sab" });
    expect(screen.getByRole("link", { name: /page$/ }).getAttribute("href")).toBe("http://nas.local:8085/sab/");
  });

  it("is not offered while adding a new client", () => {
    renderDrawer(emptyClientForm());
    expect(screen.queryByRole("link", { name: /page$/ })).toBeNull();
  });
});

describe("ClientDrawerBody page link for a client on the server only (#1142)", () => {
  const original = window.location;
  function setBrowserHost(hostname: string) {
    Object.defineProperty(window, "location", { configurable: true, value: { ...original, hostname } });
  }
  afterEach(() => Object.defineProperty(window, "location", { configurable: true, value: original }));

  function renderEditing(form: ClientForm) {
    render(
      <QueryClientProvider client={new QueryClient()}><ClientDrawerBody
        form={form} setForm={() => {}} editing={{ id: "c1", name: "NZBGet" } as never} errors={{}} clearError={() => {}}
        mappings={[]} newMapping={{ remotePath: "", localPath: "" }} setNewMapping={() => {}} busy={null}
        onAddMapping={() => {}} onRemoveMapping={() => {}} onTestMapping={() => {}} onRemove={() => {}}
      /></QueryClientProvider>
    );
  }
  const nzbget = { ...emptyClientForm(), protocol: "nzbget", host: "localhost", port: "6789", password: "secret-login" };

  it("shows the address with a plain note, not a dead link, when the browser is on another computer", () => {
    setBrowserHost("192.168.1.20");
    renderEditing(nzbget);
    expect(screen.queryByRole("link", { name: /page$/ })).toBeNull();
    expect(screen.getByText(/own page is on the computer Deluno runs on: http:\/\/localhost:6789\//)).toBeTruthy();
    expect(document.body.textContent).not.toContain("secret-login");
  });

  it("keeps the link when the browser is on that computer", () => {
    setBrowserHost("localhost");
    renderEditing(nzbget);
    expect(screen.getByRole("link", { name: /page$/ }).getAttribute("href")).toBe("http://localhost:6789/");
  });

  it("links a real address as before whatever the browser's computer", () => {
    setBrowserHost("192.168.1.20");
    renderEditing({ ...nzbget, host: "192.168.1.10" });
    expect(screen.getByRole("link", { name: /page$/ }).getAttribute("href")).toBe("http://192.168.1.10:6789/");
  });
});

describe("ClientDrawerBody the client's own warnings (#1193)", () => {
  function renderWarnings(warnings: string[] | undefined, onClear: () => void, busy: string | null = null) {
    render(
      <QueryClientProvider client={new QueryClient()}><ClientDrawerBody
        form={{ ...emptyClientForm(), protocol: "sabnzbd" }}
        setForm={() => {}}
        editing={{ id: "c1", name: "SABnzbd" } as never}
        errors={{}}
        clearError={() => {}}
        mappings={[]}
        newMapping={{ remotePath: "", localPath: "" }}
        setNewMapping={() => {}}
        busy={busy}
        onAddMapping={() => {}}
        onRemoveMapping={() => {}}
        onTestMapping={() => {}}
        onRemove={() => {}}
        warnings={warnings}
        onClearWarnings={onClear}
      /></QueryClientProvider>
    );
  }

  it("lists them and clears them through the client from the drawer", () => {
    let cleared = 0;
    renderWarnings(["Failed to connect to news.example"], () => { cleared += 1; });
    expect(screen.getByText("Failed to connect to news.example")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Clear all of SABnzbd's warnings" }));
    expect(cleared).toBe(1);
  });

  it("shows nothing when the client has no warnings", () => {
    renderWarnings([], () => {});
    expect(screen.queryByRole("button", { name: /warnings/ })).toBeNull();
  });
});

// #1248: the same dimmed Remove... with no reason as the indexer drawer.
describe("ClientDrawerBody Remove while the connection test runs (#1248)", () => {
  it("says it is waiting for the connection test to finish", () => {
    render(
      <QueryClientProvider client={new QueryClient()}><ClientDrawerBody
        form={{ ...emptyClientForm(), protocol: "qbittorrent", host: "192.168.1.10", port: "8080" }}
        setForm={() => {}}
        editing={{ id: "c1", name: "Seedbox" } as never}
        errors={{}}
        clearError={() => {}}
        mappings={[]}
        newMapping={{ remotePath: "", localPath: "" }}
        setNewMapping={() => {}}
        busy="test:form"
        onAddMapping={() => {}}
        onRemoveMapping={() => {}}
        onTestMapping={() => {}}
        onRemove={() => {}}
      /></QueryClientProvider>
    );
    expect((screen.getByRole("button", { name: "Remove…" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText("Waiting for the connection test to finish")).toBeTruthy();
  });
});
