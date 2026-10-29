import { useState } from "react";
import { useLocation } from "react-router-dom";
import { Button } from "../ui/button";
import * as Dialog from "../ui/dialog-primitives";
import { usePasswordPrompt } from "./password-prompt";
import { RecoveryCodesPanel } from "./recovery-codes-panel";
import { useRecoveryCodes } from "./recovery-codes-manager";

const DISMISSED_KEY = "deluno-recovery-prompt-dismissed";

function wasDismissed(): boolean {
  try {
    return sessionStorage.getItem(DISMISSED_KEY) === "1";
  } catch {
    return false;
  }
}

/**
 * An account made before recovery codes existed is asked, once after its next sign-in, to make and save them (#1392).
 * "Not now" holds the question until the next sign-in; making the codes ends it for good.
 */
export function RecoveryCodesPrompt() {
  const { pathname } = useLocation();
  const { status, codes, busy, error, make, finish } = useRecoveryCodes();
  const [dismissed, setDismissed] = useState(wasDismissed);
  const { ask, prompt } = usePasswordPrompt();

  async function makeWithPassword() {
    const password = await ask("Making recovery codes needs your password, because they let anyone who holds them take over your account.");
    if (password) await make(password);
  }

  // The guided setup asks for them on its own account step.
  const asking = status?.made === false && !dismissed && !pathname.startsWith("/setup-guide");
  const open = asking || codes !== null;

  function notNow() {
    try {
      sessionStorage.setItem(DISMISSED_KEY, "1");
    } catch {
      // The question simply comes back on the next page load.
    }

    setDismissed(true);
  }

  return (
    <>
    <Dialog.Root open={open} onOpenChange={(next) => (!next && codes === null ? notNow() : undefined)}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/50 backdrop-blur-[2px]" />
        <Dialog.Content
          aria-describedby={undefined}
          className="fixed left-1/2 top-1/2 z-50 w-[calc(100%-2rem)] max-w-md -translate-x-1/2 -translate-y-1/2 space-y-3 rounded-2xl border border-hairline bg-card p-6 shadow-2xl dark:border-white/8"
        >
          <Dialog.Title className="font-semibold text-foreground">Save your recovery codes</Dialog.Title>
          {codes ? (
            <RecoveryCodesPanel codes={codes} onSaved={finish} doneLabel="Done" />
          ) : (
            <>
              <p className="text-sm leading-relaxed text-muted-foreground">
                Your account has no recovery codes yet. They are the way back in if you forget your username or password.
              </p>
              {error ? (
                <p role="alert" className="text-sm text-destructive">
                  {error}
                </p>
              ) : null}
              <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
                <Button type="button" variant="secondary" size="sm" disabled={busy} onClick={notNow}>
                  Not now
                </Button>
                <Button type="button" size="sm" disabled={busy} onClick={() => void makeWithPassword()}>
                  Make recovery codes
                </Button>
              </div>
            </>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
    {prompt}
    </>
  );
}
