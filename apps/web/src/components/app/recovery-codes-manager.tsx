import { useCallback, useEffect, useState } from "react";
import { KeyRound } from "lucide-react";
import { Button } from "../ui/button";
import { ConfirmDialog } from "../ui/confirm-dialog";
import { makeRecoveryCodes, readRecoveryStatus, type RecoveryCodesStatus } from "../../lib/recovery";
import { usePasswordPrompt } from "./password-prompt";
import { RecoveryCodesPanel } from "./recovery-codes-panel";

/** Reads how many recovery codes are left and makes a new set (#1392). A new set voids the old one. */
export function useRecoveryCodes() {
  const [status, setStatus] = useState<RecoveryCodesStatus | null>(null);
  const [codes, setCodes] = useState<string[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      setStatus(await readRecoveryStatus());
    } catch (err) {
      setError((err as Error).message);
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const make = useCallback(async (currentPassword: string) => {
    setBusy(true);
    setError(null);
    try {
      setCodes(await makeRecoveryCodes(currentPassword));
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }, []);

  const finish = useCallback(() => {
    setCodes(null);
    void refresh();
  }, [refresh]);

  return { status, codes, busy, error, make, finish };
}

/** How many codes are left, a way to make a new set, and the new set shown once. */
export function RecoveryCodesManager() {
  const { status, codes, busy, error, make, finish } = useRecoveryCodes();
  const [confirming, setConfirming] = useState(false);
  const { ask, prompt } = usePasswordPrompt();

  async function makeWithPassword() {
    const password = await ask("Making recovery codes needs your password, because they let anyone who holds them take over your account.");
    if (password) await make(password);
  }

  if (codes) {
    return <RecoveryCodesPanel codes={codes} onSaved={finish} doneLabel="Done" />;
  }

  const made = status?.made ?? false;
  return (
    <div className="space-y-3">
      {error ? (
        <p role="alert" className="text-(length:--type-body-sm) text-destructive">
          {error}
        </p>
      ) : null}
      {status ? (
        <p className="text-(length:--type-body-sm) text-foreground">
          {made
            ? `${status.remaining} of 10 recovery codes left.`
            : "You have no recovery codes yet. They are the way back in if you forget your username or password."}
        </p>
      ) : null}
      <Button
        type="button"
        variant="outline"
        className="gap-2"
        disabled={busy || status === null}
        onClick={() => (made ? setConfirming(true) : void makeWithPassword())}
      >
        <KeyRound className="h-4 w-4" strokeWidth={1.75} />
        {made ? "Make new codes" : "Make recovery codes"}
      </Button>
      {prompt}
      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        title="Make new recovery codes?"
        description="The codes you have now stop working. Save the new ones before you close the next screen."
        confirmLabel="Make new codes"
        confirmVariant="default"
        presentation="default"
        busy={busy}
        onConfirm={() => {
          setConfirming(false);
          void makeWithPassword();
        }}
      />
    </div>
  );
}
