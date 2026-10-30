import { useCallback, useRef, useState, type FormEvent, type ReactNode } from "react";
import { Button } from "../ui/button";
import * as Dialog from "../ui/dialog-primitives";
import { Input } from "../ui/input";

/** The header that carries the current password to a change that asks for it (#1433). */
export const CURRENT_PASSWORD_HEADER = "X-Current-Password";

/** The headers for a request that needs the current password; an empty set when none was asked for. */
export function withCurrentPassword(password: string | null | undefined, headers: Record<string, string> = {}): Record<string, string> {
  return password ? { ...headers, [CURRENT_PASSWORD_HEADER]: password } : headers;
}

/** The plain sentence a refusal carries (a wrong or missing password, or any other problem), or the fallback. */
export async function refusalSentence(response: Response, fallback: string): Promise<string> {
  try {
    const body = (await response.clone().json()) as { message?: string; errors?: Record<string, string[]> };
    return body.message ?? Object.values(body.errors ?? {}).flat()[0] ?? fallback;
  } catch {
    return fallback;
  }
}

/**
 * Asks for the current password before a change that could hand the account to someone else (#1433): creating or
 * deleting an API key, making new recovery codes and changing who the home-network skip lets in. `ask` answers with the
 * password, or null when the person cancels; `prompt` is the dialog and goes anywhere in the page.
 */
export function usePasswordPrompt(): { ask: (reason: string) => Promise<string | null>; prompt: ReactNode } {
  const [reason, setReason] = useState<string | null>(null);
  const [password, setPassword] = useState("");
  const settle = useRef<((value: string | null) => void) | null>(null);

  const ask = useCallback((why: string) => {
    setPassword("");
    setReason(why);
    return new Promise<string | null>((resolve) => {
      settle.current = resolve;
    });
  }, []);

  function close(value: string | null) {
    settle.current?.(value);
    settle.current = null;
    setReason(null);
    setPassword("");
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    if (password) close(password);
  }

  const prompt = (
    <Dialog.Root open={reason !== null} onOpenChange={() => close(null)}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/50 backdrop-blur-[2px]" />
        <Dialog.Content
          aria-describedby="password-prompt-reason"
          className="fixed left-1/2 top-1/2 z-50 w-[calc(100%-2rem)] max-w-sm -translate-x-1/2 -translate-y-1/2 rounded-2xl border border-hairline bg-card p-6 shadow-2xl dark:border-white/8"
        >
          <form onSubmit={submit} className="space-y-3">
            <Dialog.Title className="font-semibold text-foreground">Enter your password</Dialog.Title>
            <p id="password-prompt-reason" className="text-sm leading-relaxed text-muted-foreground">
              {reason}
            </p>
            <label htmlFor="password-prompt-input" className="text-sm font-medium text-foreground">
              Current password
            </label>
            <Input
              id="password-prompt-input"
              type="password"
              autoComplete="current-password"
              autoFocus
              value={password}
              onChange={(event) => setPassword(event.target.value)}
            />
            <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
              <Button type="button" variant="secondary" size="sm" onClick={() => close(null)}>
                Cancel
              </Button>
              <Button type="submit" size="sm" disabled={!password}>
                Confirm
              </Button>
            </div>
          </form>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );

  return { ask, prompt };
}
