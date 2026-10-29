import { act, cleanup, fireEvent, render, renderHook, screen } from "@testing-library/react";
import { createMemoryRouter, MemoryRouter, RouterProvider, useLocation } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { SignalRStatus } from "../../lib/use-signalr";

// #1329: the small shell parts that say how things stand: the toasts, the connection badge, the first-run banner, the status pill and the error screen.

const sonner = vi.hoisted(() => ({ props: null as null | Record<string, any>, theme: "dark" }));
vi.mock("sonner", () => ({
  Toaster: (props: Record<string, any>) => { sonner.props = props; return <div data-testid="sonner" />; },
  toast: { dismiss: vi.fn(), success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() },
  useSonner: () => ({ toasts: [] })
}));
vi.mock("next-themes", () => ({ useTheme: () => ({ resolvedTheme: sonner.theme }) }));

const connection = vi.hoisted(() => ({ status: "connected" as SignalRStatus }));
vi.mock("../../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/use-signalr")>()),
  useSignalRStatus: () => connection.status
}));

const reloadOntoNewBuild = vi.hoisted(() => vi.fn());
vi.mock("../../lib/stale-build", async (actual) => ({ ...(await actual<typeof import("../../lib/stale-build")>()), reloadOntoNewBuild }));

const { Toaster } = await import("./toaster");
const { WsStatusBadge } = await import("./ws-status-badge");
const { OnboardingBanner } = await import("./onboarding-banner");
const { SaveStatus, useSaveStatus } = await import("./save-status");
const { RouteErrorBoundary } = await import("./route-error-boundary");

afterEach(cleanup);

describe("the toast stack's behaviour (#1329)", () => {
  it("closes each toast after 4.5 seconds, shows four at a time, and gives each a close button", () => {
    render(<Toaster />);
    expect(sonner.props).toMatchObject({ duration: 4500, visibleToasts: 4, closeButton: true, position: "bottom-right" });
  });

  it("takes the dark or light theme from the app, with an icon for every kind of toast", () => {
    sonner.theme = "dark";
    const dark = render(<Toaster />);
    expect(sonner.props!.theme).toBe("dark");
    for (const kind of ["success", "info", "warning", "error", "loading"]) expect(sonner.props!.icons[kind]).toBeTruthy();
    dark.unmount();

    sonner.theme = "light";
    render(<Toaster />);
    expect(sonner.props!.theme).toBe("light");
  });
});

describe("the live-connection badge for screen readers and small screens (#1329)", () => {
  beforeEach(() => { connection.status = "connected"; });

  it("is a polite live region for screen readers while the connection is down", () => {
    connection.status = "disconnected";
    render(<WsStatusBadge />);

    const badge = screen.getByRole("status");
    expect(badge).toHaveAttribute("aria-live", "polite");
    expect(badge.getAttribute("aria-label")).toContain("Not live: trying again");
  });

  it("is hidden below the sm breakpoint by the class the shell gives it, and shown from sm up", async () => {
    connection.status = "reconnecting";
    render(<WsStatusBadge className="hidden sm:inline-flex" />);

    const badge = screen.getByRole("status");
    expect(badge.className).toContain("hidden");
    expect(badge.className).toContain("sm:inline-flex");
  });
});

describe("the first-run banner on the Dashboard (#1329)", () => {
  function Where() {
    const location = useLocation();
    return <output data-testid="where">{location.pathname}</output>;
  }
  const open = () => {
    const router = createMemoryRouter([
      { path: "/", element: <OnboardingBanner isSetupSuppressed={false} onDismiss={vi.fn()} /> },
      { path: "*", element: <Where /> }
    ]);
    return render(<RouterProvider router={router} />);
  };

  it("says 'Build your media library' with Build my setup going to the guided setup", async () => {
    open();

    expect(screen.getByRole("heading", { name: "Build your media library" })).toBeInTheDocument();
    const link = screen.getByRole("link", { name: /Build my setup/ });
    expect(link).toHaveAttribute("href", "/setup-guide");
    fireEvent.click(link);
    expect(await screen.findByTestId("where")).toHaveTextContent("/setup-guide");
  });

  it("has Open library setup going to Settings", async () => {
    open();

    const link = screen.getByRole("link", { name: "Open library setup" });
    expect(link).toHaveAttribute("href", "/settings");
    fireEvent.click(link);
    expect(await screen.findByTestId("where")).toHaveTextContent("/settings");
  });

  it("is hidden once setup is completed or skipped, and stays hidden after it is dismissed", () => {
    const hidden = render(<MemoryRouter><OnboardingBanner isSetupSuppressed onDismiss={vi.fn()} /></MemoryRouter>);
    expect(hidden.container).toBeEmptyDOMElement();
    hidden.unmount();

    const dismissed = vi.fn();
    const shown = render(<MemoryRouter><OnboardingBanner isSetupSuppressed={false} onDismiss={dismissed} /></MemoryRouter>);
    fireEvent.click(screen.getByRole("button", { name: "Dismiss onboarding" }));
    expect(dismissed).toHaveBeenCalledTimes(1);
    expect(shown.container.querySelector("section")).toBeNull();
  });
});

describe("the status pill beside an action (#1329)", () => {
  it("shows Syncing…, then Saved just now, then fades away after 2.2 seconds, as a polite live region", () => {
    vi.useFakeTimers();
    try {
      const { result } = renderHook(() => useSaveStatus());
      const view = render(<SaveStatus state="syncing" />);
      expect(screen.getByRole("status")).toHaveTextContent("Syncing…");
      expect(screen.getByRole("status")).toHaveAttribute("aria-live", "polite");
      view.unmount();

      act(() => result.current.markSaved());
      expect(result.current.state).toBe("saved");
      const saved = render(<SaveStatus state="saved" />);
      expect(screen.getByRole("status")).toHaveTextContent("Saved just now");
      saved.unmount();

      act(() => { vi.advanceTimersByTime(2199); });
      expect(result.current.state).toBe("saved");
      act(() => { vi.advanceTimersByTime(2); });
      expect(result.current.state).toBe("idle");
      expect(render(<SaveStatus state={result.current.state} />).container).toBeEmptyDOMElement();
    } finally {
      vi.useRealTimers();
    }
  });

  it("shows an error with its message and stays until it is cleared", () => {
    vi.useFakeTimers();
    try {
      const { result } = renderHook(() => useSaveStatus());
      act(() => result.current.markError("The folder is read-only."));
      render(<SaveStatus state={result.current.state} message={result.current.message} />);
      expect(screen.getByRole("status")).toHaveTextContent("The folder is read-only.");

      act(() => { vi.advanceTimersByTime(10_000); });
      expect(result.current.state).toBe("error");
      act(() => result.current.reset());
      expect(result.current.state).toBe("idle");
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("the error screen's buttons and code (#1329)", () => {
  function Where() {
    const location = useLocation();
    return <output data-testid="where">{location.pathname}</output>;
  }
  function failWith(loader: () => never) {
    const router = createMemoryRouter([
      { path: "/", errorElement: <RouteErrorBoundary />, loader, element: <div /> },
      { path: "/system", element: <Where /> }
    ]);
    render(<RouterProvider router={router} />);
  }

  it("Try again reloads the page, Report issue opens System, and an unexpected failure carries the route:unexpected code", async () => {
    const reload = vi.fn();
    const original = window.location;
    Object.defineProperty(window, "location", { configurable: true, value: { ...original, reload } });
    try {
      failWith(() => { throw new Error("The metadata provider did not answer."); });

      expect(await screen.findByText("route:unexpected")).toBeInTheDocument();
      fireEvent.click(screen.getByRole("button", { name: "Try again" }));
      expect(reload).toHaveBeenCalledTimes(1);

      fireEvent.click(screen.getByRole("button", { name: "Report issue" }));
      expect(await screen.findByTestId("where")).toHaveTextContent("/system");
    } finally {
      Object.defineProperty(window, "location", { configurable: true, value: original });
    }
  });

  it("a failed route response carries its status as the code: route:404 says the page is missing, route:500 says the area could not load", async () => {
    failWith(() => { throw new Response("", { status: 404, statusText: "Not Found" }); });
    expect(await screen.findByText("route:404")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "That page is missing" })).toBeInTheDocument();
    cleanup();

    failWith(() => { throw new Response("", { status: 500, statusText: "The server broke" }); });
    expect(await screen.findByText("route:500")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "This area could not load" })).toBeInTheDocument();
    expect(screen.getByText("The server broke")).toBeInTheDocument();
    expect(reloadOntoNewBuild).not.toHaveBeenCalled();
  });
});
