/**
 * The decision trail, as one card on both detail pages.
 *
 * Every consequential thing Deluno does has to be explainable, so the row says
 * what it decided and the drawer says why: the inputs it weighed, the outcome,
 * and the alternatives it considered, with legacy scores shown only when an
 * older score-based decision supplied one.
 */
import { useState } from "react";
import { Chip } from "../ui/chip";
import { Drawer, DrawerFacts, DrawerFooter, DrawerSection } from "../ui/drawer";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, LIST_TRACK } from "../ui/list-card";
import type { DecisionExplanationItem } from "../../lib/api";
import { formatDateTime, useDisplayPreferences } from "../../lib/display-preferences";

export function DecisionExplanationList({ decisions }: { decisions: DecisionExplanationItem[] }) {
  const [openId, setOpenId] = useState<string | null>(null);
  const { preferences } = useDisplayPreferences();
  const open = decisions.find((item) => item.id === openId) ?? null;
  const shown = decisions.slice(0, 12);

  return (
    <>
      <ListCard
        title="Decision trail"
        count={decisions.length ? `Latest ${shown.length} of ${decisions.length}` : undefined}
      >
        {shown.length === 0 ? (
          <ListEmpty
            title="No decisions recorded yet"
            description="Searches, grabs, imports and retries land here with the inputs Deluno weighed and what it chose."
          />
        ) : (
          <ListTable
            columns={[
              { label: "Decision" },
              { label: "Scope", mobile: true },
              { label: "When" },
              { label: "Status", width: LIST_TRACK.status }
            ]}
          >
            {shown.map((item) => (
              <ListRow key={item.id} onClick={() => setOpenId(item.id)} selected={openId === item.id}>
                {/* The engine sometimes records the same sentence as both reason and
                    outcome; showing it twice in one row reads as a rendering fault. */}
                <ListNameCell name={item.reason} sub={item.outcome === item.reason ? undefined : item.outcome} />
                <ListCell primary={item.scope} mobile />
                <ListCell primary={formatDateTime(item.occurredUtc, preferences)} />
                <ListCell>
                  <Chip tone={statusTone(item.status)}>{formatStatus(item.status)}</Chip>
                </ListCell>
              </ListRow>
            ))}
          </ListTable>
        )}
      </ListCard>

      <Drawer
        open={open !== null}
        onOpenChange={(next) => {
          if (!next) setOpenId(null);
        }}
        title={open?.reason ?? "Decision"}
        description={open ? `${open.scope} · ${formatDateTime(open.occurredUtc, preferences)}` : undefined}
        footer={
          <DrawerFooter state="clean" readOnly saveLabel="Close" onCancel={() => setOpenId(null)} />
        }
      >
        {open ? (
          <>
            <DrawerSection title="Outcome" aside={formatStatus(open.status)}>
              <p className="text-[length:var(--type-body-sm)] leading-relaxed text-foreground">{open.outcome}</p>
            </DrawerSection>

            {Object.entries(open.inputs).filter(([, value]) => value).length ? (
              <DrawerSection title="What it weighed">
                <DrawerFacts
                  items={Object.entries(open.inputs)
                    .filter(([, value]) => value)
                    .map(([label, value]) => ({ label, value: value as string }))}
                />
              </DrawerSection>
            ) : null}

            {open.alternatives.length ? (
              <DrawerSection title="Alternatives" aside={`${open.alternatives.length} considered`}>
                <div className="max-h-72 overflow-y-auto pr-1">
                  <DrawerFacts
                    items={open.alternatives.map((alternative) => ({
                      label: alternative.name,
                      value: alternative.score === null
                        ? alternative.status
                        : `${alternative.status} · legacy score ${alternative.score}`
                    }))}
                  />
                </div>
                <p className="text-[length:var(--type-caption)] leading-snug text-muted-foreground">
                  {open.alternatives[0]?.reason}
                </p>
              </DrawerSection>
            ) : null}
          </>
        ) : null}
      </Drawer>
    </>
  );
}
function formatStatus(status: string) {
  const spaced = status.replace(/[-_]/g, " ");
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

function statusTone(status: string): "ok" | "warn" | "bad" | "info" {
  const normalized = status.toLowerCase();
  if (["completed", "matched", "sent", "requeued"].includes(normalized)) return "ok";
  if (["held", "checked", "planned", "started"].includes(normalized)) return "warn";
  if (["failed", "dead-letter", "blocked"].includes(normalized)) return "bad";
  return "info";
}
