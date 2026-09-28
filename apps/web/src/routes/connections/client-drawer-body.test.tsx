import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
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
