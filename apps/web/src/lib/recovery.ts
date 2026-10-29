import { authedFetch } from "./use-auth";

/** How many recovery codes are left, and whether any were ever made (#1392). */
export interface RecoveryCodesStatus {
  remaining: number;
  made: boolean;
}

/** Proof of who is asking: a recovery code. */
export type RecoveryProof = { code: string };

/** The server's own plain sentence for a code that did not work, or a try that came too soon. */
export class RecoveryRefusedError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "RecoveryRefusedError";
  }
}

const COULD_NOT_REACH = "Deluno could not be reached. Check that it is running, then try again.";

async function sentenceFrom(response: Response, fallback: string): Promise<string> {
  try {
    const body = (await response.clone().json()) as { message?: string; errors?: Record<string, string[]> };
    return body.message ?? Object.values(body.errors ?? {}).flat()[0] ?? fallback;
  } catch {
    return fallback;
  }
}

async function post(path: string, body: unknown): Promise<Response> {
  try {
    return await fetch(path, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });
  } catch {
    throw new RecoveryRefusedError(COULD_NOT_REACH);
  }
}

/** The username a working code belongs to; nothing is used up. */
export async function checkRecoveryProof(proof: RecoveryProof): Promise<string> {
  const response = await post("/api/auth/recovery/check", proof);
  if (!response.ok) {
    throw new RecoveryRefusedError(await sentenceFrom(response, "That did not work. Check it and try again."));
  }

  return ((await response.json()) as { username: string }).username;
}

/** Sets the new password with a working code, which is then used up. */
export async function resetPasswordWithProof(proof: RecoveryProof, newPassword: string): Promise<void> {
  const response = await post("/api/auth/recovery/reset", { ...proof, newPassword });
  if (!response.ok) {
    throw new RecoveryRefusedError(await sentenceFrom(response, "That did not work. Check it and try again."));
  }
}

async function signedIn(path: string, init?: RequestInit): Promise<Response> {
  try {
    return await authedFetch(path, init);
  } catch {
    throw new Error(COULD_NOT_REACH);
  }
}

export async function readRecoveryStatus(): Promise<RecoveryCodesStatus> {
  const response = await signedIn("/api/auth/recovery-codes");
  if (!response.ok) throw new Error("Could not read your recovery codes.");
  return (await response.json()) as RecoveryCodesStatus;
}

/** A new set of ten codes, shown once. Making them voids every earlier code. */
export async function makeRecoveryCodes(): Promise<string[]> {
  const response = await signedIn("/api/auth/recovery-codes", { method: "POST" });
  if (!response.ok) throw new Error("Could not make new recovery codes.");
  return ((await response.json()) as { codes: string[] }).codes;
}
