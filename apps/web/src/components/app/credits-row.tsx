/**
 * Who is in it, and who made it.
 *
 * <p>The cast section existed on both detail pages and the reader behind it was
 * written out twice, character-for-character — so the film page grew portrait
 * cards worth looking at and the show page kept its six 40px avatars, which is
 * the size you use when you have decided nobody looks. Two copies of one idea
 * drift the moment one of them is improved, so there is now one.</p>
 *
 * <p>Crew is the half that was missing entirely. A page that lists the cast and
 * stops there answers half the question; the crew is the half that says whose
 * film it is, and Deluno kept exactly one name of it — the director, which is
 * the right shape for a sort column and much too thin for a page.</p>
 */
import { useCallback, useEffect, useRef, useState } from "react";
import { ChevronLeft, ChevronRight } from "lucide-react";
import { cn } from "../../lib/utils";

export interface CreditedPerson {
  name: string;
  /** A character for a player, a job for a crew member. */
  role: string | null;
  profileUrl: string | null;
  /** The provider's person id. Absent on credits stored before it was read. */
  personId: string | null;
}

interface CreditsRowProps {
  heading: string;
  people: CreditedPerson[];
  className?: string;
}

/**
 * A row of portrait cards scrolling sideways rather than wrapping — a wall of
 * faces is the one thing on a detail page you scan rather than read, and
 * wrapping turns it into a block you have to parse.
 *
 * <p><b>Arrows AND scrolling, not arrows instead of it.</b> James asked which is
 * better; honestly, neither on its own. A scrollbar is a poor <i>signal</i> —
 * thirty faces cut off at the edge read as the end of the list, and a thin grey
 * bar under the row is the last thing an eye lands on. But replacing the scroll
 * with buttons breaks the three ways people actually move a row like this:
 * trackpad swipe, touch drag, and arrow keys after tabbing in. So the scrolling
 * stays and the scrollbar goes, and the arrows do the signalling — they appear
 * only on the side that has more to show, which is the same information the
 * scrollbar was carrying, drawn where it gets looked at.</p>
 */
export function CreditsRow({ heading, people, className }: CreditsRowProps) {
  const scroller = useRef<HTMLDivElement>(null);
  const [overflow, setOverflow] = useState({ left: false, right: false });

  const measure = useCallback(() => {
    const node = scroller.current;
    if (!node) return;
    // A pixel of slack: fractional widths mean scrollLeft rarely lands exactly
    // on the end, and an arrow that never turns off is worse than no arrow.
    setOverflow({
      left: node.scrollLeft > 1,
      right: node.scrollLeft + node.clientWidth < node.scrollWidth - 1
    });
  }, []);

  useEffect(() => {
    measure();
    const node = scroller.current;
    if (!node || typeof ResizeObserver === "undefined") return;

    const observer = new ResizeObserver(measure);
    observer.observe(node);
    return () => observer.disconnect();
  }, [measure, people]);

  // Just under a full view, so the card at the edge stays on screen as an
  // anchor rather than the row jumping to somewhere with no continuity.
  const page = (direction: -1 | 1) =>
    scroller.current?.scrollBy({ left: direction * scroller.current.clientWidth * 0.85, behavior: "smooth" });

  if (people.length === 0) return null;

  return (
    <section className={cn("border-t border-white/10 pt-5", className)}>
      <div className="flex items-center justify-between gap-3">
        <p className="text-[length:var(--type-micro)] font-bold uppercase tracking-[0.18em] text-muted-foreground">{heading}</p>
        <div className="flex items-center gap-2">
          <span className="text-[length:var(--type-caption)] text-muted-foreground">{people.length} credited</span>
          <ScrollArrow direction="left" enabled={overflow.left} heading={heading} onClick={() => page(-1)} />
          <ScrollArrow direction="right" enabled={overflow.right} heading={heading} onClick={() => page(1)} />
        </div>
      </div>
      <div ref={scroller} onScroll={measure} className="no-scrollbar -mx-1 mt-3 flex gap-3 overflow-x-auto px-1 pb-1">
        {people.map((person) => (
          <CreditCard key={`${person.name}-${person.role ?? ""}`} person={person} />
        ))}
      </div>
    </section>
  );
}

/**
 * One credit — a link when the person can be looked up, a plain card when they
 * cannot.
 *
 * <p>A face with a name under it invites a click, and until now nothing happened
 * — James: <i>"clicking on cast and crew should bring up their imdb link dont
 * you think?"</i>. The destination is the provider's own person page, because
 * the person id is the one identifier we actually hold: a name is not a link,
 * two people share one routinely, and an IMDb id would cost a separate lookup
 * per person on every metadata refresh — fifty extra upstream calls a title.</p>
 *
 * <p>Credits stored before the id was read have no `personId`, so they stay
 * plain cards rather than linking somewhere wrong. A metadata refresh fills
 * them in.</p>
 */
function CreditCard({ person }: { person: CreditedPerson }) {
  const portrait = person.profileUrl
    ? <img src={person.profileUrl} alt="" loading="lazy" className="aspect-[2/3] w-full rounded-xl border border-white/15 bg-surface-2 object-cover shadow-lg transition group-hover/credit:border-primary/50" />
    : <div className="flex aspect-[2/3] w-full items-center justify-center rounded-xl border border-white/15 bg-surface-2 text-center text-[length:var(--type-caption)] text-muted-foreground">No photo</div>;

  const caption = (
    <figcaption className="mt-2 leading-tight">
      <span className="block truncate text-xs font-semibold text-foreground" title={person.name}>{person.name}</span>
      {person.role
        ? <span className="mt-0.5 block truncate text-[length:var(--type-caption)] text-muted-foreground" title={person.role}>{person.role}</span>
        : null}
    </figcaption>
  );

  if (!person.personId) {
    return <figure className="w-[7.5rem] shrink-0">{portrait}{caption}</figure>;
  }

  return (
    <a
      href={`https://www.themoviedb.org/person/${person.personId}`}
      target="_blank"
      rel="noreferrer"
      title={`${person.name} on TMDb`}
      className="group/credit w-[7.5rem] shrink-0 no-underline"
    >
      <figure>{portrait}{caption}</figure>
    </a>
  );
}

/**
 * One arrow. Disabled rather than removed at the end of the row, so the pair
 * does not shuffle sideways every time you reach an edge.
 */
function ScrollArrow({ direction, enabled, heading, onClick }: {
  direction: "left" | "right";
  enabled: boolean;
  heading: string;
  onClick: () => void;
}) {
  const Icon = direction === "left" ? ChevronLeft : ChevronRight;

  return (
    <button
      type="button"
      onClick={onClick}
      disabled={!enabled}
      aria-label={`Scroll ${heading.toLowerCase()} ${direction}`}
      className={cn(
        "flex h-7 w-7 items-center justify-center rounded-full border border-white/15 bg-surface-2/80 transition",
        enabled ? "text-foreground hover:border-primary/40 hover:bg-surface-2" : "cursor-default text-muted-foreground/30"
      )}
    >
      <Icon className="h-4 w-4" />
    </button>
  );
}

/**
 * Read the cast and the crew out of a stored metadata blob.
 *
 * <p>Both casings are accepted because both occur: the gateway answers in camel
 * case and Deluno stores what its own record serialises, which is Pascal. A
 * reader that knew only one returned an empty list for half the installs.</p>
 */
export function readStoredCredits(metadataJson: string | null): { cast: CreditedPerson[]; crew: CreditedPerson[] } {
  if (!metadataJson) return { cast: [], crew: [] };

  try {
    const parsed = JSON.parse(metadataJson) as Record<string, unknown>;
    return {
      cast: readPeople(parsed.cast ?? parsed.Cast, ["character", "Character"]),
      crew: readPeople(parsed.crew ?? parsed.Crew, ["job", "Job"])
    };
  } catch {
    return { cast: [], crew: [] };
  }
}

function readPeople(value: unknown, roleKeys: readonly string[]): CreditedPerson[] {
  if (!Array.isArray(value)) return [];

  return value.flatMap((person) => {
    if (typeof person !== "object" || person === null) return [];
    const item = person as Record<string, unknown>;

    const name = item.name ?? item.Name;
    if (typeof name !== "string" || !name.trim()) return [];

    const role = roleKeys.map((key) => item[key]).find((candidate) => typeof candidate === "string" && candidate.trim());
    const profileUrl = item.profileUrl ?? item.ProfileUrl;
    const personId = item.personId ?? item.PersonId;

    return [{
      name: name.trim(),
      role: typeof role === "string" ? role : null,
      profileUrl: typeof profileUrl === "string" ? profileUrl : null,
      personId: typeof personId === "string" && personId.trim() ? personId : null
    }];
  });
}
