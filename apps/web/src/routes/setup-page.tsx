import { type FormEvent, useEffect, useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { Eye, EyeOff, Loader2, UserPlus } from "lucide-react";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
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

  const returnTo = params.get("return") ?? "/";

  useEffect(() => {
    if (isAuthenticated) {
      navigate(returnTo, { replace: true });
    }
  }, [isAuthenticated, navigate, returnTo]);

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
      navigate(`/setup-guide?return=${encodeURIComponent(returnTo)}`, { replace: true });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Setup failed.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="relative flex min-h-dvh flex-col items-center justify-center bg-background px-4">
      <div aria-hidden className="pointer-events-none fixed inset-0 -z-10 overflow-hidden">
        <div className="absolute -top-60 left-1/3 h-[600px] w-[600px] rounded-full bg-primary/[0.09] blur-[200px]" />
        <div className="absolute bottom-0 right-0 h-[400px] w-[400px] rounded-full bg-primary/[0.05] blur-[160px]" />
      </div>

      <div className="w-full max-w-[440px]">
        <div className="mb-8 flex flex-col items-center gap-3">
          <div
            className="flex h-14 w-14 items-center justify-center rounded-2xl shadow-lg"
            style={{
              background: "linear-gradient(135deg, hsl(var(--primary)), hsl(var(--primary-2)))",
              boxShadow: "0 8px 32px hsl(var(--primary-deep)/0.45)"
            }}
          >
            <svg width="28" height="28" viewBox="0 0 30 30" fill="none" aria-hidden>
              <rect x="5.5" y="9" width="19" height="12" rx="2.5" stroke="white" strokeWidth="1.3" strokeOpacity="0.9" />
              <rect x="5.5" y="11.25" width="2.5" height="2.25" rx="0.5" fill="white" fillOpacity="0.7" />
              <rect x="5.5" y="16.5" width="2.5" height="2.25" rx="0.5" fill="white" fillOpacity="0.7" />
              <rect x="22" y="11.25" width="2.5" height="2.25" rx="0.5" fill="white" fillOpacity="0.7" />
              <rect x="22" y="16.5" width="2.5" height="2.25" rx="0.5" fill="white" fillOpacity="0.7" />
              <path d="M13.75 12.75L19 15L13.75 17.25V12.75Z" fill="white" fillOpacity="0.95" />
            </svg>
          </div>
          <div className="text-center">
            <h1 className="font-display text-2xl font-bold tracking-tight text-foreground">
              Set up Deluno
            </h1>
            <p className="mt-1 text-[13px] text-muted-foreground">
              Create the first user account for this Deluno install.
            </p>
          </div>
        </div>

        <form
          onSubmit={(event) => void handleSubmit(event)}
          className="space-y-3 rounded-2xl border border-hairline bg-card/80 p-6 shadow-lg backdrop-blur dark:border-white/[0.06]"
        >
          {error ? (
            <div className="flex items-start gap-2.5 rounded-xl border border-destructive/25 bg-destructive/8 px-3.5 py-3 text-[13px] text-destructive dark:bg-destructive/12">
              <svg className="mt-0.5 h-4 w-4 shrink-0" viewBox="0 0 16 16" fill="none">
                <circle cx="8" cy="8" r="6.5" stroke="currentColor" strokeWidth="1.5" />
                <path d="M8 5v3.5M8 10.5v.5" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
              </svg>
              {error}
            </div>
          ) : null}

          <div className="space-y-1.5">
            <label htmlFor="setup-display-name" className="text-[11px] font-medium uppercase tracking-[0.14em] text-muted-foreground">
              Display name
            </label>
            <Input
              id="setup-display-name"
              type="text"
              autoFocus
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
              placeholder="Your name"
              className="h-[var(--control-height-lg)]"
              required
            />
          </div>

          <div className="space-y-1.5">
            <label htmlFor="setup-username" className="text-[11px] font-medium uppercase tracking-[0.14em] text-muted-foreground">
              Username
            </label>
            <Input
              id="setup-username"
              type="text"
              autoComplete="username"
              value={username}
              onChange={(event) => setUsername(event.target.value)}
              placeholder="your-name"
              className="h-[var(--control-height-lg)]"
              required
            />
          </div>

          <div className="space-y-1.5">
            <label htmlFor="setup-password" className="text-[11px] font-medium uppercase tracking-[0.14em] text-muted-foreground">
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
                className="h-[var(--control-height-lg)] pr-11"
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
            <label htmlFor="setup-confirm-password" className="text-[11px] font-medium uppercase tracking-[0.14em] text-muted-foreground">
              Confirm password
            </label>
            <Input
              id="setup-confirm-password"
              type={showPassword ? "text" : "password"}
              autoComplete="new-password"
              value={confirmPassword}
              onChange={(event) => setConfirmPassword(event.target.value)}
              placeholder="••••••••"
              className="h-[var(--control-height-lg)]"
              required
            />
          </div>

          <Button
            type="submit"
            className="mt-1 h-[var(--control-height-lg)] w-full gap-2 text-[14px] font-semibold"
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
        </form>

        <p className="mt-5 text-center text-[12px] text-muted-foreground">
          After this step, Deluno will require sign-in for every session.
        </p>
      </div>
    </div>
  );
}
