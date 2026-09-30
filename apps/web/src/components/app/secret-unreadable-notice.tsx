/**
 * #1094: a saved key, password or token that was stored on another computer
 * cannot be read here. The connection is still listed; this says so in plain
 * words on its drawer, and goes away once the secret is entered again.
 */
export function secretUnreadableText(item: { secretUnreadable?: boolean | null; secretProblem?: string | null } | null | undefined): string | null {
  if (!item?.secretUnreadable) return null;
  return item.secretProblem?.trim() || "The saved key was stored on another computer and can't be read here. Enter it again.";
}

export function SecretUnreadableNotice({ item }: { item: { secretUnreadable?: boolean | null; secretProblem?: string | null } | null | undefined }) {
  const text = secretUnreadableText(item);
  if (!text) return null;
  return (
    <div role="alert" className="rounded-[10px] border border-warning/30 bg-warning/[0.06] px-3 py-2.5">
      <p className="text-[length:var(--type-body-sm)] font-medium text-foreground">Enter the saved key again</p>
      <p className="mt-1 text-[length:var(--type-caption)] leading-relaxed text-muted-foreground">{text}</p>
    </div>
  );
}
