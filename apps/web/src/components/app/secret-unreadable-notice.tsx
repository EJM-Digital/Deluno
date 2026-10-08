/**
 * #1094: a saved key, password or token that was stored on another computer
 * cannot be read here. The connection is still listed; this says so in plain
 * words on its drawer, and goes away once the secret is entered again.
 */
export function secretUnreadableText(item: { secretUnreadable?: boolean | null; secretProblem?: string | null } | null | undefined): string | null {
  if (!item?.secretUnreadable) return null;
  return item.secretProblem?.trim() || "The saved key was stored on another computer and can't be read here. Enter it again.";
}

/**
 * True when a health message only repeats what the notice above already says (the host words it the same way when a test
 * cannot read the saved password), so a drawer says the sentence once.
 */
export function repeatsSecretNotice(item: { secretUnreadable?: boolean | null; secretProblem?: string | null } | null | undefined, message: string | null | undefined): boolean {
  const notice = secretUnreadableText(item);
  const said = message?.trim();
  if (!notice || !said) return false;
  return said === notice || said.includes(notice) || /was stored on another computer/i.test(said);
}

export function SecretUnreadableNotice({ item }: { item: { secretUnreadable?: boolean | null; secretProblem?: string | null } | null | undefined }) {
  const text = secretUnreadableText(item);
  if (!text) return null;
  return (
    <div role="alert" className="rounded-[10px] border border-warning/30 bg-warning/6 px-3 py-2.5">
      <p className="text-(length:--type-body-sm) font-medium text-foreground">Enter the saved key again</p>
      <p className="mt-1 text-(length:--type-caption) leading-relaxed text-muted-foreground">{text}</p>
    </div>
  );
}
