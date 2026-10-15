import { type FormEvent, useEffect, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { Eye, EyeOff, Loader2, UserPlus } from "lucide-react";
import { AppMark } from "../components/shell/app-mark";
import { Button } from "../components/ui/button";
import { Card } from "../components/ui/card";
import { Input } from "../components/ui/input";
import { safeReturnAddress } from "../lib/return-address";
import { describeAuthRefusal } from "../lib/sign-in-failure";
import { useAuth } from "../lib/use-auth";

export function SetupPage() {
  const { bootstrap, isAuthenticated } = useAuth();
  const navigate = useNavigate();
  const [params] = useSearchParams();

  const [displayName, setDisplayName] = useState("");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const returnTo = safeReturnAddress(params.get("return"));

  // Creating the account signs the person in, and this effect sees that before handleSubmit's own navigate runs, so
  // it has to name the same place: the guided setup, which carries the return address on (#1339).
  const guidePath = `/setup-guide?return=${encodeURIComponent(returnTo)}`;

  useEffect(() => {
    if (isAuthenticated) {
      navigate(guidePath, { replace: true });
    }
  }, [isAuthenticated, navigate, guidePath]);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setError(null);

    if (password.length < 8) {
      setError("Use at least 8 characters for the password.");
      return;
    }

    if (password !== confirmPassword) {
      setError("Passwords do not match.");
      return;
    }

    setBusy(true);
    try {
      await bootstrap(displayName.trim(), username.trim(), password);
      navigate(guidePath, { replace: true });
    } catch (err) {
      setError(await describeAuthRefusal(err, "create the first account"));
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

      <div className="w-full max-w-[440px]">
        <div className="mb-8 flex flex-col items-center gap-3">
          <AppMark size={56} />
          <div className="text-center">
            <h1 className="font-display text-2xl font-bold tracking-tight text-foreground">
              Set up Deluno
            </h1>
            <p className="mt-1 text-(length:--type-body-sm) text-muted-foreground">
              Create the first user account for this Deluno install.
            </p>
          </div>
        </div>

        <Card
          as="form"
          onSubmit={(event) => void handleSubmit(event)}
          className="space-y-3 bg-card/80 p-6 shadow-lg backdrop-blur-sm dark:border-white/6"
        >
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
            <label htmlFor="setup-display-name" className="text-(length:--type-caption) font-medium uppercase tracking-[0.14em] text-muted-foreground">
              Display name
            </label>
            <Input
              id="setup-display-name"
              type="text"
              autoFocus
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
              placeholder="Your name"
              className="h-(--control-height-lg)"
              required
            />
          </div>

          <div className="space-y-1.5">
            <label htmlFor="setup-username" className="text-(length:--type-caption) font-medium uppercase tracking-[0.14em] text-muted-foreground">
              Username
            </label>
            <Input
              id="setup-username"
              type="text"
              autoComplete="username"
              value={username}
              onChange={(event) => setUsername(event.target.value)}
              placeholder="your-name"
              className="h-(--control-height-lg)"
              required
            />
          </div>

          <div className="space-y-1.5">
            <label htmlFor="setup-password" className="text-(length:--type-caption) font-medium uppercase tracking-[0.14em] text-muted-foreground">
              Password
            </label>
            <div className="relative">
              <Input
                id="setup-password"
                type={showPassword ? "text" : "password"}
                autoComplete="new-password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                placeholder="••••••••"
                className="h-(--control-height-lg) pr-11"
                required
              />
              <button
                type="button"
                onClick={() => setShowPassword((value) => !value)}
                className="absolute right-3 top-1/2 -translate-y-1/2 rounded-md p-1 text-muted-foreground transition hover:text-foreground"
                aria-label={showPassword ? "Hide password" : "Show password"}
              >
                {showPassword ? (
                  <EyeOff className="h-4 w-4" strokeWidth={1.75} />
                ) : (
                  <Eye className="h-4 w-4" strokeWidth={1.75} />
                )}
              </button>
            </div>
          </div>

          <div className="space-y-1.5">
            <label htmlFor="setup-confirm-password" className="text-(length:--type-caption) font-medium uppercase tracking-[0.14em] text-muted-foreground">
              Confirm password
            </label>
            <Input
              id="setup-confirm-password"
              type={showPassword ? "text" : "password"}
              autoComplete="new-password"
              value={confirmPassword}
              onChange={(event) => setConfirmPassword(event.target.value)}
              placeholder="••••••••"
              className="h-(--control-height-lg)"
              required
            />
          </div>

          <Button
            type="submit"
            className="mt-1 h-(--control-height-lg) w-full gap-2 text-(length:--type-body-sm) font-semibold"
            disabled={
              busy ||
              !displayName.trim() ||
              !username.trim() ||
              !password ||
              !confirmPassword
            }
          >
            {busy ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              <UserPlus className="h-4 w-4" strokeWidth={2} />
            )}
            {busy ? "Creating account…" : "Create account"}
          </Button>
        </Card>

        <p className="mt-5 text-center text-(length:--type-caption) text-muted-foreground">
          After this step, Deluno will require sign-in for every session.
        </p>
      </div>
    </div>
  );
}
