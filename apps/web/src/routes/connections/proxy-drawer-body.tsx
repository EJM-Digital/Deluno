import { SecretUnreadableNotice } from "../../components/app/secret-unreadable-notice";
import { removeUnavailableReason } from "./busy-reason";
import type { ProxyItem } from "../../lib/api";
import { Button } from "../../components/ui/button";
import { DrawerDanger, DrawerSection } from "../../components/ui/drawer";
import { Field, FieldRow } from "../../components/ui/field";
import { Input } from "../../components/ui/input";
import { Select } from "../../components/ui/select";
import { SwitchRow } from "../../components/ui/switch";
import type { ProxyForm } from "./forms";
import { PreferencesLink } from "./preferences-link";

const PROXY_TYPE_OPTIONS = [
  { value: "http", label: "HTTP" },
  { value: "socks4", label: "SOCKS4" },
  { value: "socks5", label: "SOCKS5" },
  { value: "flaresolverr", label: "FlareSolverr / Byparr" }
];

/**
 * A single proxy's own settings (#748), kept to what an indexer's drawer
 * needs to point at one — type, address, and optional credentials.
 */
export function ProxyDrawerBody({
  form,
  setForm,
  editing,
  errors,
  clearError,
  busy,
  onRemove
}: {
  form: ProxyForm;
  setForm: (updater: (current: ProxyForm) => ProxyForm) => void;
  editing: ProxyItem | null;
  errors: Record<string, string>;
  clearError: (key: string) => void;
  busy: string | null;
  onRemove: () => void;
}) {
  return (
    <>
      <SecretUnreadableNotice item={editing} />
      <DrawerSection title="Basics">
        <Field label="Type" help="Deluno names the proxy from its type and address.">
          <Select value={form.proxyType} onChange={(event) => setForm((current) => ({ ...current, proxyType: event.target.value as ProxyForm["proxyType"] }))} options={PROXY_TYPE_OPTIONS} />
        </Field>
        <Field
          label="Address"
          error={errors.address}
          help={form.proxyType === "flaresolverr" ? <>The FlareSolverr or Byparr address, e.g. http://127.0.0.1:8191. This is a Cloudflare helper for the indexers assigned to it only. One helper for every tracker is set in Preferences, General, and this one wins for its own indexers. <PreferencesLink /></> : "The proxy address, e.g. socks5://192.168.1.1:1080 or http://proxy.example.test:8080."}
        >
          <Input value={form.address} onChange={(event) => { clearError("address"); setForm((current) => ({ ...current, address: event.target.value })); }} className="font-mono text-(length:--type-caption)" placeholder={form.proxyType === "flaresolverr" ? "http://127.0.0.1:8191" : form.proxyType === "http" ? "http://proxy.example.test:8080" : form.proxyType === "socks4" ? "socks4://192.168.1.1:1080" : "socks5://192.168.1.1:1080"} autoComplete="off" spellCheck={false} />
        </Field>
        {form.proxyType !== "flaresolverr" ? (
          <FieldRow>
            <Field label="Username" help="Only if this proxy requires authentication.">
              <Input value={form.username} onChange={(event) => setForm((current) => ({ ...current, username: event.target.value }))} autoComplete="off" />
            </Field>
            <Field label="Password" help={editing ? "Leave empty to keep the current password; type a new one to rotate it." : "Only if this proxy requires authentication."}>
              <Input type="password" value={form.password} onChange={(event) => setForm((current) => ({ ...current, password: event.target.value }))} placeholder={editing ? "••••••••••••  (unchanged)" : ""} autoComplete="off" />
            </Field>
          </FieldRow>
        ) : null}
        <SwitchRow label="Enabled" description="Indexers assigned to this proxy will use it." checked={form.isEnabled} onCheckedChange={(checked) => setForm((current) => ({ ...current, isEnabled: checked }))} />
      </DrawerSection>

      {editing ? (
        <DrawerSection title="Used by">
          <p className="text-(length:--type-body-sm) text-muted-foreground">
            {editing.assignedIndexerNames.length
              ? `${editing.assignedIndexerNames.length} indexer${editing.assignedIndexerNames.length === 1 ? "" : "s"}: ${editing.assignedIndexerNames.join(", ")}`
              : "No indexer is assigned to this proxy yet. Assign it from that indexer's own drawer."}
          </p>
        </DrawerSection>
      ) : null}

      {editing ? (
        <DrawerSection>
          <DrawerDanger
            unavailableReason={removeUnavailableReason(busy)}
            title="Remove this proxy"
            description="Indexers assigned to it go back to a direct connection."
            action={<Button type="button" variant="destructive" size="sm" onClick={onRemove} disabled={busy !== null}>Remove…</Button>}
          />
        </DrawerSection>
      ) : null}
    </>
  );
}
