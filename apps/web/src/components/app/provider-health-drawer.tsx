import type { IntegrationFailure } from "../../lib/api";
import { formatDateTime, useDisplayPreferences } from "../../lib/display-preferences";
import { Chip, type ChipProps } from "../ui/chip";
import { Drawer, DrawerFacts, DrawerFooter, DrawerSection } from "../ui/drawer";

export interface ProviderHealthItem {
  id: string;
  name: string;
  protocol: string;
  healthStatus: string;
  lastHealthMessage: string | null;
  lastHealthFailure?: IntegrationFailure | null;
  lastHealthLatencyMs?: number | null;
  lastHealthTestUtc?: string | null;
}

export interface ProviderHealthSelection {
  kind: "indexer" | "download-client";
  provider: ProviderHealthItem;
}

export function ProviderHealthDrawer({
  selection,
  onClose
}: {
  selection: ProviderHealthSelection | null;
  onClose: () => void;
}) {
  const { preferences } = useDisplayPreferences();
  const provider = selection?.provider ?? null;
  const failure = provider?.lastHealthFailure ?? null;
  const kindLabel = selection?.kind === "indexer" ? "Search source" : "Download client";

  return (
    <Drawer
      open={selection !== null}
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
      title={provider?.name ?? "Provider health"}
      description={provider ? `${kindLabel} · ${provider.protocol}` : undefined}
      footer={<DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={onClose} />}
    >
      {provider ? (
        <>
          <DrawerSection title="Health result" aside={<Chip tone={healthTone(provider.healthStatus)}>{provider.healthStatus}</Chip>}>
            <DrawerFacts items={[
              { label: "Last checked", value: provider.lastHealthTestUtc ? formatDateTime(provider.lastHealthTestUtc, preferences) : "Not tested" },
              provider.lastHealthLatencyMs != null ? { label: "Response time", value: `${provider.lastHealthLatencyMs} ms` } : null,
              provider.lastHealthMessage ? { label: "Reported message", value: provider.lastHealthMessage } : null
            ].filter((item): item is { label: string; value: string } => item !== null)} />
          </DrawerSection>

          {failure ? (
            <>
              <DrawerSection title="Why it needs attention" aside={retryLabel(failure.retryState)}>
                <div className="grid gap-2">
                  <p className="text-[length:var(--type-body-sm)] font-medium leading-relaxed text-foreground">{failure.summary || failure.message}</p>
                  {failure.message && failure.message !== failure.summary ? <p className="text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{failure.message}</p> : null}
                  {failure.upstreamDetail ? <p className="whitespace-pre-wrap text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">Provider detail: {failure.upstreamDetail}</p> : null}
                </div>
                <DrawerFacts items={[
                  { label: "Failure type", value: failureKindLabel(failure.kind) },
                  failure.serviceName ? { label: "Service", value: failure.serviceName } : null,
                  failure.operation ? { label: "Action", value: formatOperation(failure.operation) } : null,
                  failure.code ? { label: "Code", value: failure.code, mono: true } : null,
                  failure.httpStatus != null ? { label: "HTTP status", value: String(failure.httpStatus) } : null,
                  failure.attempts > 0 ? { label: "Attempts", value: String(failure.attempts) } : null,
                  failure.retryAfterUtc ? { label: "Next eligible", value: formatDateTime(failure.retryAfterUtc, preferences) } : null
                ].filter((item): item is { label: string; value: string; mono?: boolean } => item !== null)} />
              </DrawerSection>

              <DrawerSection title="What happens next">
                <p className="text-[length:var(--type-body-sm)] leading-relaxed text-foreground">{retryExplanation(failure)}</p>
                <p className="text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{failure.nextAction || "Open the provider connection and test it again after correcting the issue."}</p>
              </DrawerSection>
            </>
          ) : (
            <DrawerSection title="What happens next">
              <p className="text-[length:var(--type-body-sm)] leading-relaxed text-foreground">
                {provider.lastHealthMessage ?? (provider.lastHealthTestUtc
                  ? "The last check did not record a typed failure."
                  : "Deluno has not recorded a test result for this provider yet.")}
              </p>
              <p className="text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">
                Open the provider connection to run a new test or update its settings.
              </p>
            </DrawerSection>
          )}
        </>
      ) : null}
    </Drawer>
  );
}

function healthTone(status: string): NonNullable<ChipProps["tone"]> {
  if (status === "healthy" || status === "ok") return "ok";
  if (status === "degraded" || status === "warning" || status === "rate-limited") return "warn";
  if (status === "unhealthy" || status === "failed" || status === "unreachable") return "bad";
  return "idle";
}

function failureKindLabel(kind: string) {
  return formatOperation(kind || "Unknown failure");
}

function formatOperation(value: string) {
  const spaced = value.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/[._-]/g, " ");
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

function retryLabel(retryState: string) {
  switch (retryState) {
    case "Retrying": return "Retrying automatically";
    case "RetryScheduled": return "Retry scheduled";
    case "CircuitOpen": return "Paused after repeated failures";
    case "ManualAction": return "Needs your action";
    case "NotRetryable": return "Will not retry automatically";
    default: return retryState || "Retry state unknown";
  }
}

function retryExplanation(failure: IntegrationFailure) {
  switch (failure.retryState) {
    case "Retrying": return "Deluno is retrying this request automatically.";
    case "RetryScheduled": return failure.retryAfterUtc
      ? "Deluno will retry automatically when the next eligible time arrives."
      : "Deluno has scheduled an automatic retry.";
    case "CircuitOpen": return "Deluno paused automatic requests after repeated failures to avoid making the provider problem worse.";
    case "ManualAction": return "Deluno will not retry this terminal result automatically.";
    case "NotRetryable": return "Deluno will not retry this action automatically.";
    default: return "Deluno has not reported whether this action will retry automatically.";
  }
}
