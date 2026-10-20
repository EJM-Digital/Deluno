import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { ProxyDrawerBody } from "./proxy-drawer-body";
import { ConnectionTestResult } from "./connection-test-result";
import { emptyProxyForm } from "./forms";

function drawer(form = emptyProxyForm()) {
  return render(
    <MemoryRouter>
      <ProxyDrawerBody form={form} setForm={() => {}} editing={null} errors={{}} clearError={() => {}} busy={null} onRemove={() => {}} />
    </MemoryRouter>
  );
}

/** #1076: a proxy is named by Deluno from its type and address, so its drawer has no Name box. */
describe("ProxyDrawerBody", () => {
  it("has no Name box, only the type, address and switches", () => {
    drawer();
    expect(screen.queryByLabelText("Name")).toBeNull();
    expect(screen.getByLabelText("Type")).toBeTruthy();
    expect(screen.getByLabelText("Address")).toBeTruthy();
  });

  // #1143: the example in the address box matches the type chosen.
  it("shows a FlareSolverr address, not a SOCKS one, once FlareSolverr / Byparr is chosen", () => {
    drawer({ ...emptyProxyForm(), proxyType: "flaresolverr" });
    expect(screen.getByLabelText("Address")).toHaveAttribute("placeholder", "http://127.0.0.1:8191");
    expect(screen.getByRole("link", { name: "Open Preferences, General" })).toHaveAttribute("href", "/settings/general");
  });

  it("shows a SOCKS example for a SOCKS proxy", () => {
    drawer({ ...emptyProxyForm(), proxyType: "socks5" });
    expect(screen.getByLabelText("Address")).toHaveAttribute("placeholder", "socks5://192.168.1.1:1080");
  });
});

describe("ConnectionTestResult (#1143)", () => {
  it("links a message that sends the person to Preferences, General", () => {
    render(
      <MemoryRouter>
        <ConnectionTestResult result={{ healthStatus: "degraded", message: "1337x is behind a Cloudflare check. A helper, set in Preferences, General, gets Deluno past it." }} />
      </MemoryRouter>
    );
    expect(screen.getByRole("link", { name: "Open Preferences, General" })).toHaveAttribute("href", "/settings/general");
  });

  it("adds no link to an ordinary message", () => {
    render(
      <MemoryRouter>
        <ConnectionTestResult result={{ healthStatus: "healthy", message: "Reached yts.gg" }} />
      </MemoryRouter>
    );
    expect(screen.queryByRole("link")).toBeNull();
  });
});

// C.11.9: the unreadable-saved-key notice shows in the proxy drawer, and only when the saved password cannot be read here.
describe("ProxyDrawerBody unreadable saved password notice", () => {
  const render_ = (editing: unknown) => render(
    <MemoryRouter>
      <ProxyDrawerBody form={emptyProxyForm()} setForm={() => {}} editing={editing as never} errors={{}} clearError={() => {}} busy={null} onRemove={() => {}} />
    </MemoryRouter>
  );

  it("says to enter the key again, with the server's reason, when the saved password cannot be read", () => {
    render_({ id: "p1", assignedIndexerNames: [], secretUnreadable: true, secretProblem: "The saved proxy password was stored on another computer." });
    const notice = screen.getByRole("alert");
    expect(notice).toHaveTextContent("Enter the saved key again");
    expect(notice).toHaveTextContent("The saved proxy password was stored on another computer.");
  });

  it("says nothing for a proxy whose password can be read, or a new one", () => {
    render_({ id: "p1", assignedIndexerNames: [], secretUnreadable: false });
    expect(screen.queryByText("Enter the saved key again")).toBeNull();
  });
});
