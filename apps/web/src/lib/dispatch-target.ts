/**
 * A release chosen by hand on the Search page is recorded against a generated
 * id, not a title (#1062). Screens show that in plain words instead of the id.
 */
export const CHOSEN_ON_SEARCH_LABEL = "Chosen on Search";

export function isChosenOnSearch(entityType: string | null | undefined) {
  return entityType === "release";
}

/** The two-part label a "what was this for" row shows: type, then id. */
export function relatedEntityLabel(entityType: string | null | undefined, entityId: string | null | undefined) {
  if (isChosenOnSearch(entityType)) {
    return { primary: CHOSEN_ON_SEARCH_LABEL, secondary: undefined as string | undefined };
  }
  return { primary: entityType ?? "—", secondary: entityId ?? undefined };
}
