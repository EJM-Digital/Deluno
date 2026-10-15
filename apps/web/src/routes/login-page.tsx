/**
 * Deluno login page.
 *
 * Deluno runs as a sign-in-required application. Brand-new installs are
 * redirected to the first-run setup flow before this page is shown.
 */

import { type FormEvent, useEffect, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { Eye, EyeOff, Loader2, LogIn } from "lucide-react";
import { readSignOutReason, useAuth } from "../lib/use-auth";
import { AppMark } from "../components/shell/app-mark";
import { Button } from "../components/ui/button";
import { Checkbox } from "../components/ui/checkbox";
import { Card } from "../components/ui/card";
import { Input } from "../components/ui/input";
import { cn } from "../lib/utils";
import { safeReturnAddress } from "../lib/return-address";
import { describeAuthRefusal } from "../lib/sign-in-failure";

export function LoginPage() {
  const { isAuthenticated, login } = useAuth();
  const navigate = useNavigate();
  const [params] = useSearchParams();

  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [showPw, setShowPw] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [keepSignedIn, setKeepSignedIn] = useState(false);
  const [endedReason] = useState(readSignOutReason);

  const returnTo = safeReturnAddress(params.get("return"));

  useEffect(() => {
    if (isAuthenticated) {
      navigate(returnTo, { replace: true });
    }
  }, [isAuthenticated, navigate, returnTo]);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);

    try {
      await login(username.trim(), password, keepSignedIn);
      navigate(returnTo, { replace: true });
    } catch (err) {
      setError(await describeAuthRefusal(err, "sign you in"));
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

      <div className="w-full max-w-[400px]">
        <div className="mb-8 flex flex-col items-center gap-3">
          <AppMark size={56} />
          <div className="text-center">
            <h1 className="font-display text-2xl font-bold tracking-tight text-foreground">
              Sign in to Deluno
            </h1>
            <p className="mt-1 text-(length:--type-body-sm) text-muted-foreground">
              Your account is required to access this Deluno install.
            </p>
          </div>
        </div>

        <Card
          as="form"
          onSubmit={(event) => void handleSubmit(event)}
          className="space-y-3 bg-card/80 p-6 shadow-lg backdrop-blur-sm dark:border-white/6"
        >
          {endedReason && !error ? (
            <div
              role="status"
              className="rounded-xl border border-hairline px-3.5 py-3 text-(length:--type-body-sm) text-muted-foreground"
            >
              {endedReason}
            </div>
          ) : null}

          {error ? (
            <div className="flex items-start gap-2.5 rounded-xl border border-destructive/25 px-3.5 py-3 text-(length:--type-body-sm) text-destructive">
              <svg className="mt-0.5 h-4 w-4 shrink-0" viewBox="0 0 16 16" fill="none">
                <circle cx="8" cy="8" r="6.5" stroke="currentColor" strokeWidth="1.5" />
                <path d="M8 5v3.5M8 10.5v.5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
              </svg>
              {error}
            </div>
          ) : null}

          <div className="space-y-1.5">
            <label htmlFor="login-username" className="text-(length:--type-caption) font-medium uppercase tracking-[0.14em] text-muted-foreground">
              Username
            </label>
            <Input
              id="login-username"
              type="text"
              autoComplete="username"
              autoFocus
              value={username}
              onChange={(event) => setUsername(event.target.value)}
              placeholder="user"
              className="h-(--control-height-lg)"
              required
            />
          </div>

          <div className="space-y-1.5">
            <label htmlFor="login-password" className="text-(length:--type-caption) font-medium uppercase tracking-[0.14em] text-muted-foreground">
              Password
            </label>
            <div className="relative">
              <Input
                id="login-password"
                type={showPw ? "text" : "password"}
                autoComplete="current-password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                placeholder="••••••••"
                className="h-(--control-height-lg) pr-11"
                required
              />
              <button
                type="button"
                onClick={() => setShowPw((value) => !value)}
                className="absolute right-3 top-1/2 -translate-y-1/2 rounded-md p-1 text-muted-foreground transition hover:text-foreground"
                aria-label={showPw ? "Hide password" : "Show password"}
              >
                {showPw ? (
                  <EyeOff className="h-4 w-4" strokeWidth={1.75} />
                ) : (
                  <Eye className="h-4 w-4" strokeWidth={1.75} />
                )}
              </button>
            </div>
          </div>

          <label
            htmlFor="login-keep"
            className="flex cursor-pointer items-center gap-2.5 text-(length:--type-body-sm) text-muted-foreground"
          >
            <Checkbox id="login-keep" checked={keepSignedIn} onCheckedChange={setKeepSignedIn} />
            Keep me signed in on this device (30 days)
          </label>

          <Button
            type="submit"
            className={cn("mt-1 h-11 w-full gap-2 text-(length:--type-body-sm) font-semibold")}
            disabled={busy || !username || !password}
          >
            {busy ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              <LogIn className="h-4 w-4" strokeWidth={2} />
            )}
            {busy ? "Signing in…" : "Sign in"}
          </Button>
        </Card>

        <p className="mt-5 text-center text-(length:--type-caption) text-muted-foreground">
          Sign in with the account you created during setup.
        </p>
      </div>
    </div>
  );
}
