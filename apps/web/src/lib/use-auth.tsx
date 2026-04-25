import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode
} from "react";

export interface UserProfile {
  id: string;
  username: string;
  displayName: string;
  avatarInitials: string;
  createdUtc: string;
}

interface LoginPayload {
  accessToken: string;
  user: UserProfile;
}

interface AuthContextValue {
  user: UserProfile | null;
  token: string | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  login(username: string, password: string): Promise<void>;
  bootstrap(displayName: string, username: string, password: string): Promise<void>;
  changePassword(currentPassword: string, newPassword: string): Promise<void>;
  logout(): void;
}

const TOKEN_KEY = "deluno-auth-token";
const USER_KEY = "deluno-auth-user";

export function readStored(): { token: string | null; user: UserProfile | null } {
  try {
    const token = sessionStorage.getItem(TOKEN_KEY);
    const raw = sessionStorage.getItem(USER_KEY);
    const user = raw ? (JSON.parse(raw) as UserProfile) : null;
    return { token, user };
  } catch {
    return { token: null, user: null };
  }
}

function writeStored(token: string, user: UserProfile) {
  try {
    sessionStorage.setItem(TOKEN_KEY, token);
    sessionStorage.setItem(USER_KEY, JSON.stringify(user));
  } catch {
    // noop
  }
}

function applyLoginPayload(
  payload: LoginPayload,
  setToken: (value: string | null) => void,
  setUser: (value: UserProfile | null) => void
) {
  setToken(payload.accessToken);
  setUser(payload.user);
  writeStored(payload.accessToken, payload.user);
}

export function clearStored() {
  try {
    sessionStorage.removeItem(TOKEN_KEY);
    sessionStorage.removeItem(USER_KEY);
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
  if (response.status === 401) {
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
  }, []);

  useEffect(() => {
    const originalFetch = window.fetch.bind(window);

    window.fetch = ((input: RequestInfo | URL, init?: RequestInit) => {
      const requestUrl =
        typeof input === "string"
          ? input
          : input instanceof URL
            ? input.toString()
            : input.url;

      const isApiRequest =
        requestUrl.startsWith("/api/") ||
        requestUrl.startsWith("/hubs/") ||
        requestUrl.startsWith(`${window.location.origin}/api/`) ||
        requestUrl.startsWith(`${window.location.origin}/hubs/`);

      if (!isApiRequest) {
        return originalFetch(input, init);
      }

      const headers = new Headers(init?.headers);
      const nextToken = token ?? readStored().token;
      if (nextToken && !headers.has("Authorization")) {
        headers.set("Authorization", `Bearer ${nextToken}`);
      }

      return originalFetch(input, { ...init, headers }).then(handleUnauthorizedResponse);
    }) as typeof window.fetch;

    return () => {
      window.fetch = originalFetch;
    };
  }, [token]);

  const login = useCallback(async (username: string, password: string) => {
    const res = await fetch("/api/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password })
    });

    if (!res.ok) {
      const text = await res.text().catch(() => "");
      throw new Error(text || "Invalid credentials");
    }

    const data = (await res.json()) as LoginPayload;
    applyLoginPayload(data, setToken, setUser);
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
    setToken(null);
    setUser(null);
    clearStored();
    redirectToLogin();
  }, []);

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

  return fetch(url, { ...init, headers }).then(handleUnauthorizedResponse);
}

