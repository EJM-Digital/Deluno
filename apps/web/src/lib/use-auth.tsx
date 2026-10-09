import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode
} from "react";
import { noteRealtimePosition } from "./realtime-position";

export interface UserProfile {
  id: string;
  username: string;
  displayName: string;
  avatarInitials: string;
  createdUtc: string;
}

interface LoginPayload {
  accessToken: string;
  expiresUtc: string;
  user: UserProfile;
}

interface AuthContextValue {
  user: UserProfile | null;
  token: string | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  login(username: string, password: string, keepSignedIn?: boolean): Promise<void>;
  bootstrap(displayName: string, username: string, password: string): Promise<void>;
  changePassword(currentPassword: string, newPassword: string): Promise<void>;
  logout(): void;
}

const TOKEN_KEY = "deluno-auth-token";
const USER_KEY = "deluno-auth-user";
const EXPIRES_KEY = "deluno-auth-expires";
const KEEP_KEY = "deluno-auth-keep";
const REASON_KEY = "deluno-signout-reason";
const RENEWED_EVENT = "deluno-token-renewed";
const RENEWED_TOKEN_HEADER = "X-Deluno-Access-Token";
const RENEWED_EXPIRES_HEADER = "X-Deluno-Access-Token-Expires";

export function readStored(): { token: string | null; user: UserProfile | null } {
  try {
    const token = localStorage.getItem(TOKEN_KEY);
    const raw = localStorage.getItem(USER_KEY);
    const user = raw ? (JSON.parse(raw) as UserProfile) : null;
    return { token, user };
  } catch {
    return { token: null, user: null };
  }
}

function writeStored(token: string, user: UserProfile, expiresUtc?: string, keepSignedIn?: boolean) {
  try {
    localStorage.setItem(TOKEN_KEY, token);
    localStorage.setItem(USER_KEY, JSON.stringify(user));
    if (expiresUtc) localStorage.setItem(EXPIRES_KEY, expiresUtc);
    if (keepSignedIn !== undefined) localStorage.setItem(KEEP_KEY, keepSignedIn ? "1" : "0");
    localStorage.removeItem(REASON_KEY);
  } catch {
    // noop
  }
}

/**
 * Why the last session ended, in plain words, or null when we cannot tell.
 * A token past its stored expiry ended by time; anything else means the
 * password changed or the person signed out somewhere else.
 */
export function describeSessionEnd(
  expiresUtc: string | null,
  keptSignedIn: boolean,
  now: Date = new Date()
): string {
  const expires = expiresUtc ? Date.parse(expiresUtc) : Number.NaN;
  if (Number.isFinite(expires) && expires <= now.getTime()) {
    return keptSignedIn
      ? "You were signed out after 30 days away from Deluno. Sign in to carry on."
      : "You were signed out after 7 days away from Deluno. Sign in to carry on.";
  }

  return "You were signed out because your password changed or you signed out on another device. Sign in to carry on.";
}

/** The reason the sign-in page shows, kept until the next successful sign-in. */
export function readSignOutReason(): string | null {
  try {
    return localStorage.getItem(REASON_KEY);
  } catch {
    return null;
  }
}

function rememberSignOutReason() {
  try {
    if (!localStorage.getItem(TOKEN_KEY)) return;
    localStorage.setItem(
      REASON_KEY,
      describeSessionEnd(localStorage.getItem(EXPIRES_KEY), localStorage.getItem(KEEP_KEY) === "1")
    );
  } catch {
    // noop
  }
}

/**
 * Sliding renewal: the server hands back a fresh token once a session is past
 * halfway. Store it and tell the provider so the page keeps going.
 */
export function applyRenewedToken(response: Response) {
  const renewed = response.headers.get(RENEWED_TOKEN_HEADER);
  if (!renewed) return;
  try {
    localStorage.setItem(TOKEN_KEY, renewed);
    const expires = response.headers.get(RENEWED_EXPIRES_HEADER);
    if (expires) localStorage.setItem(EXPIRES_KEY, expires);
  } catch {
    // noop
  }
  if (typeof window !== "undefined") {
    window.dispatchEvent(new CustomEvent(RENEWED_EVENT, { detail: renewed }));
  }
}

function applyLoginPayload(
  payload: LoginPayload,
  setToken: (value: string | null) => void,
  setUser: (value: UserProfile | null) => void,
  keepSignedIn?: boolean
) {
  setToken(payload.accessToken);
  setUser(payload.user);
  writeStored(payload.accessToken, payload.user, payload.expiresUtc, keepSignedIn);
}

export function clearStored() {
  try {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(USER_KEY);
    localStorage.removeItem(EXPIRES_KEY);
    localStorage.removeItem(KEEP_KEY);
  } catch {
    // noop
  }
}

function redirectToLogin() {
  if (typeof window === "undefined") {
    return;
  }

  const returnTo = `${window.location.pathname}${window.location.search}${window.location.hash}`;
  const target =
    returnTo === "/login" ? "/login" : `/login?return=${encodeURIComponent(returnTo)}`;
  window.location.replace(target);
}

function handleUnauthorizedResponse(response: Response): Response {
  applyRenewedToken(response);
  if (response.status === 401) {
    rememberSignOutReason();
    clearStored();
    redirectToLogin();
  }

  return response;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [token, setToken] = useState<string | null>(null);
  const [user, setUser] = useState<UserProfile | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  useEffect(() => {
    const stored = readStored();
    if (stored.token && stored.user) {
      setToken(stored.token);
      setUser(stored.user);
    }

    setIsLoading(false);

    const onRenewed = (event: Event) => {
      const renewed = (event as CustomEvent<string>).detail;
      if (renewed) setToken(renewed);
    };
    window.addEventListener(RENEWED_EVENT, onRenewed);
    return () => window.removeEventListener(RENEWED_EVENT, onRenewed);
  }, []);

  const login = useCallback(async (username: string, password: string, keepSignedIn = false) => {
    const res = await fetch("/api/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password, keepSignedIn })
    });

    if (!res.ok) {
      const text = await res.text().catch(() => "");
      throw new Error(text || "Invalid credentials");
    }

    const data = (await res.json()) as LoginPayload;
    applyLoginPayload(data, setToken, setUser, keepSignedIn);
  }, []);

  const bootstrap = useCallback(async (displayName: string, username: string, password: string) => {
    const res = await fetch("/api/auth/bootstrap", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ displayName, username, password })
    });

    if (!res.ok) {
      const text = await res.text().catch(() => "");
      throw new Error(text || "Setup failed.");
    }

    const data = (await res.json()) as LoginPayload;
    applyLoginPayload(data, setToken, setUser);
  }, []);

  const changePassword = useCallback(async (currentPassword: string, newPassword: string) => {
    const res = await authedFetch("/api/auth/password", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ currentPassword, newPassword })
    });

    if (!res.ok) {
      const text = await res.text().catch(() => "");
      throw new Error(text || "Password could not be changed.");
    }
  }, []);

  const logout = useCallback(() => {
    const nextToken = token ?? readStored().token;
    if (nextToken) {
      void fetch("/api/auth/logout", {
        method: "POST",
        headers: { Authorization: `Bearer ${nextToken}` }
      }).catch(() => undefined);
    }

    setToken(null);
    setUser(null);
    clearStored();
    redirectToLogin();
  }, [token]);

  const value = useMemo<AuthContextValue>(
    () => ({
      user,
      token,
      isAuthenticated: user !== null,
      isLoading,
      login,
      bootstrap,
      changePassword,
      logout
    }),
    [user, token, isLoading, login, bootstrap, changePassword, logout]
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) {
    return {
      user: null,
      token: null,
      isAuthenticated: false,
      isLoading: false,
      login: async () => {
        throw new Error("AuthProvider not mounted");
      },
      bootstrap: async () => {
        throw new Error("AuthProvider not mounted");
      },
      changePassword: async () => {
        throw new Error("AuthProvider not mounted");
      },
      logout: () => {}
    };
  }

  return ctx;
}

export function authedFetch(
  url: string,
  init?: RequestInit,
  token?: string | null
): Promise<Response> {
  const headers = new Headers(init?.headers);
  const effectiveToken = token ?? readStored().token;
  if (effectiveToken) {
    headers.set("Authorization", `Bearer ${effectiveToken}`);
  }

  return fetch(url, { ...init, headers }).then((response) => {
    noteRealtimePosition(response);
    return handleUnauthorizedResponse(response);
  });
}

