import * as Dialog from "../ui/dialog-primitives";
import { ListPlus } from "lucide-react";
import { useEffect, useState } from "react";
import { fetchJson, readValidationProblem } from "../../lib/api";
import { authedFetch } from "../../lib/use-auth";
import { Badge } from "../ui/badge";
import { Button } from "../ui/button";
import { Field } from "../ui/field";
import { Input } from "../ui/input";
import { Select } from "../ui/select";

interface AddableRule {
  id: string;
  name: string;
  level: string;
  isEnabled: boolean;
}

/** Mirrors `Deluno.Retention.RetentionAddable`. */
interface Addable {
  entityId: string;
  level: string;
  title: string;
  year: number | null;
  alreadyListed: boolean;
  kept: boolean;
}

interface RetentionAddTitleDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  rules: AddableRule[];
  /** Called after a title was added, with what to say about it. */
  onAdded: (message: string) => void;
}

/**
 * Add a title to a rule's Leaving soon list by hand (#1008). It is marked
 * Manual, waits the rule's days like any other, and the rule's steps no longer
 * decide whether it stays. Taking one off by hand is Keep, from that rule.
 */
export function RetentionAddTitleDialog({ open, onOpenChange, rules, onAdded }: RetentionAddTitleDialogProps) {
  const usable = rules.filter((rule) => rule.isEnabled);
  const [ruleId, setRuleId] = useState("");
  const [query, setQuery] = useState("");
  const [found, setFound] = useState<Addable[]>([]);
  const [searching, setSearching] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setRuleId(usable[0]?.id ?? "");
    setQuery("");
    setFound([]);
    setProblem(null);
    // Reopening starts from the first rule that is on; the rule list itself does not reset it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  useEffect(() => {
    if (!open || !ruleId || query.trim().length < 2) {
      setFound([]);
      return;
    }
    let cancelled = false;
    setSearching(true);
    const timer = window.setTimeout(async () => {
      try {
        const results = await fetchJson<Addable[]>(`/api/retention/rules/${ruleId}/titles?query=${encodeURIComponent(query.trim())}`).catch(() => []);
        if (!cancelled) setFound(results);
      } finally {
        if (!cancelled) setSearching(false);
      }
    }, 300);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [open, ruleId, query]);

  async function add(item: Addable) {
    setBusy(item.entityId);
    setProblem(null);
    try {
      const response = await authedFetch(`/api/retention/rules/${ruleId}/titles`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ entityId: item.entityId })
      });
      if (!response.ok) {
        const invalid = await readValidationProblem(response.clone()).catch(() => null);
        throw new Error(invalid?.errors ? Object.values(invalid.errors).flat().join(" ") : `${item.title} could not be added.`);
      }
      onAdded(`${item.title} is on “${usable.find((rule) => rule.id === ruleId)?.name ?? "the rule"}” by hand.`);
      onOpenChange(false);
    } catch (error) {
      setProblem(error instanceof Error ? error.message : `${item.title} could not be added.`);
    } finally {
      setBusy(null);
    }
  }

  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/55 backdrop-blur-[2px] data-[state=open]:animate-fade-in" />
        <Dialog.Content
          className="fixed left-1/2 top-1/2 z-50 w-[calc(100%-2rem)] max-w-xl -translate-x-1/2 -translate-y-1/2 overflow-hidden rounded-2xl border border-hairline bg-card shadow-2xl data-[state=open]:animate-fade-in"
          aria-describedby="retention-add-description"
        >
          <div className="border-b border-hairline px-6 py-5">
            <div className="flex items-start gap-3">
              <span className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-primary/10">
                <ListPlus className="h-4 w-4 text-primary" />
              </span>
              <div>
                <Dialog.Title className="font-semibold text-foreground">Add a title by hand</Dialog.Title>
                <Dialog.Description id="retention-add-description" className="mt-1 text-(length:--type-body-sm) leading-relaxed text-muted-foreground">
                  It joins the rule's Leaving soon list marked Manual and waits the rule's days. The rule's steps no longer decide whether it stays; keeping it, or taking it off, does.
                </Dialog.Description>
              </div>
            </div>
          </div>

          <div className="grid gap-(--grid-gap) p-6">
            {usable.length === 0 ? (
              <p className="text-(length:--type-body-sm) text-muted-foreground">Turn a rule on first. Only a rule that is on has a list.</p>
            ) : (
              <>
                <Field label="Add it to">
                  <Select value={ruleId} onChange={(event) => setRuleId(event.target.value)} options={usable.map((rule) => ({ value: rule.id, label: rule.name }))} />
                </Field>
                <Field label="Find a title" help="Movies, shows, seasons or episodes, whichever the rule cleans out. Only titles Deluno holds files for.">
                  <Input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Start typing a name" autoComplete="off" />
                </Field>
                {problem ? <p className="text-(length:--type-caption) text-warning" role="alert">{problem}</p> : null}
                {searching ? <p className="text-(length:--type-caption) text-muted-foreground">Looking…</p> : null}
                {!searching && query.trim().length >= 2 && found.length === 0 ? (
                  <p className="text-(length:--type-caption) text-muted-foreground">Nothing with files matches that.</p>
                ) : null}
                {found.length > 0 ? (
                  <ul className="grid max-h-64 gap-1 overflow-y-auto">
                    {found.map((item) => (
                      <li key={item.entityId} className="flex items-center justify-between gap-3 rounded-lg border border-hairline px-3 py-2">
                        <span className="min-w-0 truncate text-(length:--type-body-sm) text-foreground">
                          {item.title}
                          {item.year && item.level === "movie" ? ` (${item.year})` : ""}
                        </span>
                        {item.kept ? (
                          <Badge>Kept</Badge>
                        ) : item.alreadyListed ? (
                          <Badge variant="info">On the list</Badge>
                        ) : (
                          <Button type="button" size="sm" variant="outline" onClick={() => void add(item)} disabled={busy !== null} aria-label={`Add ${item.title} to the list`}>
                            Add
                          </Button>
                        )}
                      </li>
                    ))}
                  </ul>
                ) : null}
              </>
            )}
          </div>

          <div className="flex justify-end gap-2 border-t border-hairline bg-surface-1/60 px-6 py-4">
            <Dialog.Close asChild>
              <Button type="button" variant="ghost">Close</Button>
            </Dialog.Close>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
