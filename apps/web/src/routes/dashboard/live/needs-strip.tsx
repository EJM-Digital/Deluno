/**
 * Needs you (#1096): one compact strip under the Right now band. Every problem a
 * person has to settle is counted here once, grouped by kind and by title ("17
 * stuck downloads", "Pioneer One: 9 episodes waiting to import", "2 hand-offs
 * refused"), and each group has one uniform Review button that opens a drawer
 * listing the items with their own buttons (Search again, Retry, Give up). There
 * is no card per problem, and a problem is never also a token on the journey.
 *
 * The groups are made from the same problem cards the journey used to carry, so
 * the count agrees with the tab number and the bell (`computeAttention`).
 */
import { AlertOctagon, CheckCircle2 } from "lucide-react";
import { useState } from "react";
import { Link } from "react-router-dom";
import { Button } from "../../../components/ui/button";
import { Drawer, DrawerSection } from "../../../components/ui/drawer";
import { cn } from "../../../lib/utils";
import { CARD_BUTTON, type ActionState } from "../downloads-kit";
import { CardButtons } from "./card";
import { routeOf, type JourneyCard } from "./journey";
import { faceOf, namedTitle, type TitleLookup } from "./titles";

type Kind = "handoff" | "job" | "stuck" | "review" | "request";

const WORDS: Record<Kind, { one: string; many: string; show: (count: number) => string }> = {
  handoff: { one: "hand-off refused", many: "hand-offs refused", show: (count) => `${count === 1 ? "hand-off" : "hand-offs"} refused` },
  job: { one: "import failed", many: "imports failed", show: (count) => `${count === 1 ? "import" : "imports"} failed` },
  stuck: { one: "stuck download", many: "stuck downloads", show: (count) => `${count === 1 ? "episode" : "episodes"} stuck` },
  review: { one: "import to review", many: "imports to review", show: (count) => `${count === 1 ? "episode" : "episodes"} waiting to import` },
  request: { one: "request could not be searched", many: "requests could not be searched", show: (count) => `${count === 1 ? "request" : "requests"} not searched` }
};

/** Most urgent first: a refused hand-off, a failed import, a stopped download, an import to review, a request that was not searched. */
const ORDER: Kind[] = ["handoff", "job", "stuck", "review", "request"];

function kindOf(card: JourneyCard): Kind {
  if (card.failedHandoffId) return "handoff";
  if (card.importJobId) return "job";
  if (card.recovery) return "review";
  if (card.queueRef) return "stuck";
  return "request";
}

/** The show a problem belongs to: the library's name for it, else the name read from an episode release ("Pioneer.One.S01E02" is Pioneer One). */
function showOf(card: JourneyCard, titles: TitleLookup): string | null {
  if (card.entity && card.entity.kind !== "movie") {
    const named = namedTitle(card.entity, titles, card.release);
    if (named) return named.name;
  }
  const read = /^(.+?)\s+S\d{1,2}E\d{1,3}\b/i.exec(card.title);
  return read ? read[1].replace(/\s*\(\d{4}\)$/, "") : null;
}

export interface NeedsGroup {
  key: string;
  /** "17 stuck downloads", "Pioneer One: 9 episodes waiting to import". */
  label: string;
  kind: Kind;
  cards: JourneyCard[];
}

/** Problems grouped by kind, and within a kind by show when a show has more than one. */
export function needsGroups(cards: JourneyCard[], titles: TitleLookup): NeedsGroup[] {
  const groups: NeedsGroup[] = [];
  for (const kind of ORDER) {
    const ofKind = cards.filter((card) => kindOf(card) === kind);
    if (ofKind.length === 0) continue;
    const byShow = new Map<string, JourneyCard[]>();
    for (const card of ofKind) {
      const show = showOf(card, titles);
      if (show) byShow.set(show, [...(byShow.get(show) ?? []), card]);
    }
    const shows = [...byShow.entries()].filter(([, list]) => list.length > 1).sort((a, b) => b[1].length - a[1].length);
    const grouped = new Set(shows.flatMap(([, list]) => list));
    for (const [show, list] of shows) {
      groups.push({ key: `${kind}:${show}`, kind, cards: list, label: `${show}: ${list.length.toLocaleString("en-AU")} ${WORDS[kind].show(list.length)}` });
    }
    const rest = ofKind.filter((card) => !grouped.has(card));
    if (rest.length > 0) groups.push({ key: `${kind}:rest`, kind, cards: rest, label: `${rest.length.toLocaleString("en-AU")} ${rest.length === 1 ? WORDS[kind].one : WORDS[kind].many}` });
  }
  return groups;
}

/** At most this many groups sit in the strip; the rest are one "and N more" button that opens them all. */
const MAX_CHIPS = 3;

function Items({ cards, titles, action }: { cards: JourneyCard[]; titles: TitleLookup; action: ActionState }) {
  return (
    <ul className="divide-y divide-hairline">
      {cards.map((card) => {
        const face = faceOf(card, titles);
        const said = card.alsoProblems ?? [card.reason ?? card.state.label];
        const route = routeOf(card) || card.routeNote || "";
        return (
          <li key={card.key} className="flex items-center gap-3 py-2.5">
            <div className="min-w-0 flex-1">
              <Link to={face.to} className="block truncate text-[length:var(--type-body-sm)] font-semibold text-foreground hover:text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring" title={card.release ?? face.title}>{face.title}</Link>
              {said.map((line, index) => <span key={`${line}:${index}`} title={card.reasonFull ?? undefined} className="block text-[length:var(--type-caption)] leading-snug text-destructive">{line}</span>)}
              {route ? <span className="block truncate text-[length:var(--type-micro)] text-muted-foreground">{route}</span> : null}
            </div>
            <CardButtons card={card} action={action} />
          </li>
        );
      })}
    </ul>
  );
}

export function NeedsStrip({ cards, total, titles, action }: { cards: JourneyCard[]; /** The number the tab and the bell show. */ total: number; titles: TitleLookup; action: ActionState }) {
  const groups = needsGroups(cards, titles);
  const [open, setOpen] = useState<string | null>(null);
  const shown = groups.slice(0, MAX_CHIPS);
  const rest = groups.slice(MAX_CHIPS);
  const listed = cards.reduce((sum, card) => sum + (card.alsoProblems?.length ?? 1), 0);
  const unlisted = Math.max(0, total - listed);
  const opened = open === "all" ? groups : groups.filter((group) => group.key === open);
  return (
    <section aria-label="Needs you" className={cn("flex shrink-0 items-center gap-3 rounded-2xl border bg-card px-[var(--card-pad-x)] py-2 shadow-card dark:border-white/[0.07]", groups.length > 0 || unlisted > 0 ? "border-destructive/30" : "border-hairline")}>
      {groups.length === 0 && unlisted === 0 ? (
        <>
          <CheckCircle2 aria-hidden className="h-4 w-4 shrink-0 text-success" />
          <p className="text-[length:var(--type-body-sm)] text-muted-foreground"><span className="font-semibold text-foreground">Nothing needs you.</span> No download is stuck and no import or hand-off has failed.</p>
        </>
      ) : (
        <>
          <span className="flex shrink-0 items-center gap-2 text-[length:var(--type-micro)] font-semibold uppercase tracking-[0.08em] text-destructive">
            <AlertOctagon aria-hidden className="h-4 w-4" />
            Needs you
            <span className="text-[length:var(--type-body-sm)] tabular-nums">{total.toLocaleString("en-AU")}</span>
          </span>
          <ul className="flex min-w-0 flex-1 flex-wrap items-center gap-2">
            {shown.map((group) => (
              <li key={group.key} className="flex items-center gap-2 rounded-lg border border-destructive/25 bg-destructive/[0.05] py-1 pl-2.5 pr-1">
                <span className="text-[length:var(--type-body-sm)] text-foreground">{group.label}</span>
                <Button type="button" size="sm" variant="outline" className={CARD_BUTTON} aria-label={`Review ${group.label}`} onClick={() => setOpen(group.key)}>Review</Button>
              </li>
            ))}
            {rest.length > 0 ? (
              <li className="flex items-center gap-2 rounded-lg border border-hairline py-1 pl-2.5 pr-1">
                <span className="text-[length:var(--type-body-sm)] text-muted-foreground">and {(rest.reduce((sum, group) => sum + group.cards.length, 0) + unlisted).toLocaleString("en-AU")} more</span>
                <Button type="button" size="sm" variant="outline" className={CARD_BUTTON} aria-label="Review all that needs you" onClick={() => setOpen("all")}>Review</Button>
              </li>
            ) : null}
            {unlisted > 0 && rest.length === 0 ? (
              <li className="flex items-center gap-2 rounded-lg border border-hairline py-1 pl-2.5 pr-1">
                <span className="text-[length:var(--type-body-sm)] text-muted-foreground">{unlisted.toLocaleString("en-AU")} more in Transfers</span>
                <Button asChild size="sm" variant="outline" className={CARD_BUTTON}>
                  <Link to="/queue" aria-label="Open Transfers for the rest">Open</Link>
                </Button>
              </li>
            ) : null}
          </ul>
        </>
      )}
      <Drawer
        open={open !== null && opened.length > 0}
        onOpenChange={(next) => { if (!next) setOpen(null); }}
        title={open === "all" ? "Everything that needs you" : opened[0]?.label ?? "Needs you"}
        description="Settle each one here, or open Transfers for more."
        footer={<Button asChild variant="outline" size="sm"><Link to="/queue">Open Transfers</Link></Button>}
      >
        {opened.map((group) => (
          <DrawerSection key={group.key} title={open === "all" ? group.label : "What needs you"}>
            <Items cards={group.cards} titles={titles} action={action} />
          </DrawerSection>
        ))}
      </Drawer>
    </section>
  );
}
