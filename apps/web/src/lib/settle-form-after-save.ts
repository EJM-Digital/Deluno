/**
 * After a save the form takes the server's version of every field, so a value the server tidied shows tidied. A field
 * the person kept typing in while the save ran keeps what they typed instead: the save answers the form as it was
 * submitted, and writing it over a newer edit loses that edit and leaves the Save button off for a change that was made (#1231).
 */
export function settleFormAfterSave<T extends object>(current: T, submitted: T, settled: T): T {
  const next = { ...current };
  for (const key of Object.keys(settled) as Array<keyof T>) {
    if (current[key] === submitted[key]) next[key] = settled[key];
  }
  return next;
}
