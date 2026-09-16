import type { DrawerSaveState } from "../components/ui/drawer";

/**
 * What a drawer's footer should say, given what the last save did and whether
 * the form has been touched since.
 *
 * This existed as the same expression copied into seventeen settings screens,
 * and it had the precedence wrong in every one of them: `dirty` was tested
 * before `saveState`, so a save the server had just refused still rendered as
 * "Unsaved changes". A form is always dirty at the moment a save fails - the
 * content that failed is still sitting in it - so the refusal was never once
 * the thing on screen.
 *
 * Proved on the rig: creating a library whose root is a file returns HTTP 400,
 * and pressing the button produced no message, no toast, and no change. The
 * error was set in state and then discarded before it could render.
 *
 * A refusal therefore outranks dirtiness. It clears when the next save starts,
 * not when the owner touches a field, because the reason it failed is the only
 * thing explaining why nothing happened.
 */
export function resolveDrawerSaveState(
  saveState: Exclude<DrawerSaveState, "clean" | "dirty"> | undefined,
  dirty: boolean,
): DrawerSaveState {
  if (saveState === "saving") return "saving";
  if (saveState === "error") return "error";
  return dirty ? "dirty" : saveState ?? "clean";
}

/**
 * Whether a finished save badge should be dropped because the form moved on.
 *
 * "Saved" goes stale the moment the owner edits again. "Error" does not: it is
 * the answer to "why did nothing happen when I pressed the button", and it
 * stays until the next attempt replaces it.
 */
export function shouldClearSaveState(
  saveState: Exclude<DrawerSaveState, "clean" | "dirty"> | undefined,
  dirty: boolean,
): boolean {
  return dirty && saveState === "saved";
}
