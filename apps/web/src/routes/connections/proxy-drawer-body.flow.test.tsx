import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import type { ProxyItem } from "../../lib/api";
import { emptyProxyForm, type ProxyForm } from "./forms";
import { ProxyDrawerBody } from "./proxy-drawer-body";

/** A proxy's drawer on a real form: what each box says, does to the form, and when Remove is available. */

function saved(overrides: Partial<ProxyItem> = {}): ProxyItem {
  return {
    id: "p1",
    name: "http proxy",
    proxyType: "http",
    address: "http://proxy.example.test:8080",
    hasPassword: true,
    isEnabled: true,
    createdUtc: "2026-10-01T00:00:00Z",
    updatedUtc: "2026-10-01T00:00:00Z",
    assignedIndexerNames: [],
    ...overrides
  };
}

function open(options: { form?: Partial<ProxyForm>; editing?: ProxyItem | null; errors?: Record<string, string>; busy?: string | null } = {}) {
  const seen = { form: { ...emptyProxyForm(), ...options.form } };
  const clearError = vi.fn();
  const onRemove = vi.fn();
  function Live() {
    const [form, setForm] = useState(seen.form);
    seen.form = form;
    return (
      <MemoryRouter>
        <ProxyDrawerBody
          form={form}
          setForm={setForm}
          editing={options.editing ?? null}
          errors={options.errors ?? {}}
          clearError={clearError}
          busy={options.busy ?? null}
          onRemove={onRemove}
        />
      </MemoryRouter>
    );
  }
  render(<Live />);
  return { seen, clearError, onRemove };
}

describe("the proxy type", () => {
  it("offers the four kinds, in order", () => {
    open();
    const options = within(screen.getByLabelText("Type")).getAllByRole("option");
    expect(options.map((option) => [option.getAttribute("value"), option.textContent])).toEqual([
      ["http", "HTTP"],
      ["socks4", "SOCKS4"],
      ["socks5", "SOCKS5"],
      ["flaresolverr", "FlareSolverr / Byparr"]
    ]);
    expect(screen.getByLabelText("Type")).toHaveValue("http");
  });

  it("changes only the type when another is picked", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { address: "keep:1" } });
    await user.selectOptions(screen.getByLabelText("Type"), "socks4");
    expect(seen.form).toMatchObject({ proxyType: "socks4", address: "keep:1" });
  });
});

describe("the proxy address", () => {
  it.each([
    ["http", "http://proxy.example.test:8080"],
    ["socks4", "socks4://192.168.1.1:1080"],
    ["socks5", "socks5://192.168.1.1:1080"],
    ["flaresolverr", "http://127.0.0.1:8191"]
  ] as const)("shows an example for %s", (proxyType, example) => {
    open({ form: { proxyType } });
    expect(screen.getByLabelText("Address")).toHaveAttribute("placeholder", example);
  });

  it("gives a plain-proxy example in the help, and no link to Preferences", () => {
    open({ form: { proxyType: "socks5" } });
    expect(screen.getByText("The proxy address, e.g. socks5://192.168.1.1:1080 or http://proxy.example.test:8080.")).toBeInTheDocument();
    expect(screen.queryByRole("link")).not.toBeInTheDocument();
  });

  it("explains the Cloudflare helper, and links to where one for every tracker is set", () => {
    open({ form: { proxyType: "flaresolverr" } });
    expect(screen.getByText(/This is a Cloudflare helper for the indexers assigned to it only\./)).toBeInTheDocument();
    expect(screen.queryByText(/The proxy address, e\.g\./)).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open Preferences, General" })).toHaveAttribute("href", "/settings/general");
  });

  it("is typed as it is read: no autofill, no spell check, fixed width", () => {
    open();
    const box = screen.getByLabelText("Address");
    expect(box).toHaveAttribute("autocomplete", "off");
    expect(box).toHaveAttribute("spellcheck", "false");
    expect(box).toHaveClass("font-mono");
  });

  it("changes only the address when it is typed, and clears its error", async () => {
    const user = userEvent.setup();
    const { seen, clearError } = open({ form: { username: "me" } });
    await user.type(screen.getByLabelText("Address"), "h");
    expect(clearError).toHaveBeenCalledWith("address");
    expect(seen.form).toMatchObject({ address: "h", username: "me" });
  });

  it("shows the problem with the address, and none when there is none", () => {
    open({ errors: { address: "Add the address Deluno should send requests through." } });
    expect(screen.getByText("Add the address Deluno should send requests through.")).toBeInTheDocument();
  });

  it("shows no problem when there is none", () => {
    open();
    expect(screen.queryByText(/Add the address/)).not.toBeInTheDocument();
  });
});

describe("the proxy login", () => {
  it("offers a username and password for a plain proxy and changes only that field when typed", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { address: "keep:1" } });
    await user.type(screen.getByLabelText("Username"), "me");
    expect(seen.form).toMatchObject({ username: "me", password: "", address: "keep:1" });
    await user.type(screen.getByLabelText("Password"), "pw");
    expect(seen.form).toMatchObject({ username: "me", password: "pw", address: "keep:1" });
    expect(screen.getByLabelText("Password")).toHaveAttribute("type", "password");
    expect(screen.getByLabelText("Username")).toHaveAttribute("autocomplete", "off");
    expect(screen.getByLabelText("Password")).toHaveAttribute("autocomplete", "off");
  });

  it("does not offer a login for the Cloudflare helper, which has none", () => {
    open({ form: { proxyType: "flaresolverr" } });
    expect(screen.queryByLabelText("Username")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Password")).not.toBeInTheDocument();
  });

  it("asks for a login only if the proxy needs one, on a new proxy", () => {
    open();
    expect(screen.getAllByText("Only if this proxy requires authentication.")).toHaveLength(2);
    expect(screen.getByLabelText("Password")).toHaveAttribute("placeholder", "");
  });

  it("says a saved password is kept unless a new one is typed, on an existing proxy", () => {
    open({ editing: saved() });
    expect(screen.getByText("Leave empty to keep the current password; type a new one to rotate it.")).toBeInTheDocument();
    expect(screen.getByLabelText("Password")).toHaveAttribute("placeholder", "••••••••••••  (unchanged)");
  });
});

describe("the proxy switch", () => {
  it("shows whether it is enabled and changes only that when toggled", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { isEnabled: true, address: "keep:1" } });
    const toggle = screen.getByRole("switch", { name: /Enabled/ });
    expect(toggle).toBeChecked();
    expect(screen.getByText("Indexers assigned to this proxy will use it.")).toBeInTheDocument();
    await user.click(toggle);
    expect(seen.form).toMatchObject({ isEnabled: false, address: "keep:1" });
    expect(screen.getByRole("switch", { name: /Enabled/ })).not.toBeChecked();
  });
});

describe("an existing proxy", () => {
  it("lists the indexers that use it, with a count", () => {
    open({ editing: saved({ assignedIndexerNames: ["1337x", "YTS"] }) });
    expect(screen.getByText("Used by")).toBeInTheDocument();
    expect(screen.getByText("2 indexers: 1337x, YTS")).toBeInTheDocument();
  });

  it("says one indexer in the singular", () => {
    open({ editing: saved({ assignedIndexerNames: ["1337x"] }) });
    expect(screen.getByText("1 indexer: 1337x")).toBeInTheDocument();
  });

  it("says no indexer uses it yet, and where to assign one", () => {
    open({ editing: saved() });
    expect(screen.getByText("No indexer is assigned to this proxy yet. Assign it from that indexer's own drawer.")).toBeInTheDocument();
  });

  it("can be removed, and says what happens to the indexers", async () => {
    const user = userEvent.setup();
    const { onRemove } = open({ editing: saved() });
    expect(screen.getByText("Remove this proxy")).toBeInTheDocument();
    expect(screen.getByText("Indexers assigned to it go back to a direct connection.")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Remove…" }));
    expect(onRemove).toHaveBeenCalledTimes(1);
  });

  it("cannot be removed while something else is happening, and says what it is waiting for", () => {
    open({ editing: saved(), busy: "save" });
    expect(screen.getByRole("button", { name: "Remove…" })).toBeDisabled();
    expect(screen.getByText("Waiting for the save to finish")).toBeInTheDocument();
  });
});

describe("a new proxy", () => {
  it("has no list of users and no Remove", () => {
    open();
    expect(screen.queryByText("Used by")).not.toBeInTheDocument();
    expect(screen.queryByText("Remove this proxy")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Remove…" })).not.toBeInTheDocument();
  });
});
