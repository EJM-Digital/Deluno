/**
 * Get back into Deluno (#1392): a recovery code shows the username and sets a
 * new password. The code is used up by the new password, and every device is signed out.
 */

import { type FormEvent, useState } from "react";
import { Link } from "react-router-dom";
import { Loader2, KeyRound } from "lucide-react";
import { AppMark } from "../components/shell/app-mark";
import { Button } from "../components/ui/button";
import { Card } from "../components/ui/card";
import { Input } from "../components/ui/input";
import {
  checkRecoveryProof,
  resetPasswordWithProof,
  type RecoveryProof
} from "../lib/recovery";

type Stage = "proof" | "password" | "done";

const LABEL = "text-(length:--type-caption) font-medium uppercase tracking-[0.14em] text-muted-foreground";

export function RecoveryPage() {
  const [secret, setSecret] = useState("");
  const [stage, setStage] = useState<Stage>("proof");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const proof = (): RecoveryProof => ({ code: secret.trim() });

  async function handleCheck(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      setUsername(await checkRecoveryProof(proof()));
      setStage("password");
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function handleReset(event: FormEvent) {
    event.preventDefault();
    setError(null);
    if (password.length < 8) {
      setError("Use at least 8 characters for the new password.");
      return;
    }

    if (password !== confirm) {
      setError("The two passwords do not match.");
      return;
    }

    setBusy(true);
    try {
      await resetPasswordWithProof(proof(), password);
      setStage("done");
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="relative flex min-h-dvh flex-col items-center justify-center bg-background px-4">
      <div aria-hidden className="pointer-events-none fixed inset-0 -z-10 overflow-hidden">
        <div className="absolute -top-60 left-1/3 h-[600px] w-[600px] rounded-full bg-primary/9 blur-[200px]" />
        <div className="absolute bottom-0 right-0 h-[400px] w-[400px] rounded-full bg-primary/5 blur-[160px]" />
      </div>

      <main className="w-full max-w-[400px]">
        <div className="mb-8 flex flex-col items-center gap-3">
          <AppMark size={56} />
          <div className="text-center">
            <h1 className="font-display text-2xl font-bold tracking-tight text-foreground">Get back into Deluno</h1>
            <p className="mt-1 text-(length:--type-body-sm) text-muted-foreground">
              {stage === "proof" ? "Enter a recovery code to find your username and set a new password." : null}
              {stage === "password" ? "Choose a new password." : null}
              {stage === "done" ? "Your password is changed." : null}
            </p>
          </div>
        </div>

        <Card
          as="form"
          onSubmit={(event) => void (stage === "proof" ? handleCheck(event) : handleReset(event))}
          className="space-y-3 bg-card/80 p-6 shadow-lg backdrop-blur-sm dark:border-white/6"
        >
          {error ? (
            <div role="alert" className="rounded-xl border border-destructive/25 px-3.5 py-3 text-(length:--type-body-sm) text-destructive">
              {error}
            </div>
          ) : null}

          {stage === "proof" ? (
            <>
              <div className="space-y-1.5">
                <label htmlFor="recovery-secret" className={LABEL}>
                  Recovery code
                </label>
                <Input
                  id="recovery-secret"
                  type="text"
                  autoComplete="off"
                  autoFocus
                  value={secret}
                  onChange={(event) => setSecret(event.target.value)}
                  placeholder="ABCD-EFGH-JKMN"
                  className="h-(--control-height-lg) font-mono"
                  required
                />
              </div>
              <p className="text-(length:--type-caption) text-muted-foreground">
                If you have lost both your password and your recovery codes, the only way forward is a fresh install.
              </p>
              <Button type="submit" className="mt-1 h-11 w-full gap-2 font-semibold" disabled={busy || !secret.trim()}>
                {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <KeyRound className="h-4 w-4" strokeWidth={2} />}
                {busy ? "Checking…" : "Check code"}
              </Button>
            </>
          ) : null}

          {stage === "password" ? (
            <>
              <p className="text-(length:--type-body-sm) text-foreground">
                Your username is <strong className="font-semibold">{username}</strong>.
              </p>
              <div className="space-y-1.5">
                <label htmlFor="recovery-password" className={LABEL}>
                  New password
                </label>
                <Input
                  id="recovery-password"
                  type="password"
                  autoComplete="new-password"
                  autoFocus
                  value={password}
                  onChange={(event) => setPassword(event.target.value)}
                  className="h-(--control-height-lg)"
                  required
                />
              </div>
              <div className="space-y-1.5">
                <label htmlFor="recovery-confirm" className={LABEL}>
                  Confirm new password
                </label>
                <Input
                  id="recovery-confirm"
                  type="password"
                  autoComplete="new-password"
                  value={confirm}
                  onChange={(event) => setConfirm(event.target.value)}
                  className="h-(--control-height-lg)"
                  required
                />
              </div>
              <Button type="submit" className="mt-1 h-11 w-full gap-2 font-semibold" disabled={busy || !password || !confirm}>
                {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
                {busy ? "Saving…" : "Set new password"}
              </Button>
            </>
          ) : null}

          {stage === "done" ? (
            <>
              <p role="status" className="text-(length:--type-body-sm) text-foreground">
                Your password is changed and every device was signed out. Sign in as <strong className="font-semibold">{username}</strong> with
                the new password.
              </p>
              <Button asChild className="mt-1 h-11 w-full font-semibold">
                <Link to="/login">Go to sign in</Link>
              </Button>
            </>
          ) : null}
        </Card>

        {stage !== "done" ? (
          <p className="mt-5 text-center text-(length:--type-caption) text-muted-foreground">
            <Link to="/login" className="underline underline-offset-2 hover:text-foreground">
              Back to sign in
            </Link>
          </p>
        ) : null}
      </main>
    </div>
  );
}
