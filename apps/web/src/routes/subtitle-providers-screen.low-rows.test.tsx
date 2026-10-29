import { act, cleanup, render, screen } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ConfigAreas } from "../lib/config-areas";

// #1329: the Subtitle providers list follows a change made elsewhere: a settings change for the subtitles area reads it again.

vi.mock("../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("sonner", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

type Handler = (name: string, payload: unknown) => void;
const handlers = vi.hoisted(() => [] as Handler[]);
vi.mock("../lib/use-signalr", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/use-signalr")>()),
  useSignalREvents: (_events: readonly string[], _subjects: unknown, handler: Handler) => { handlers.push(handler); }
}));

const { SubtitleProvidersPage } = await import("./subtitle-providers-screen");

const provider = { key: "subdl", displayName: "SubDL", description: "A place subtitles come from.", scope: "movies", needsUsername: false, needsPassword: false, needsApiKey: true, credentialsOptional: false, configured: null };

function show() {
  const router = createMemoryRouter([{ path: "/", loader: () => ({ providers: [provider] }), element: <SubtitleProvidersPage /> }], { initialEntries: ["/"] });
  render(<RouterProvider router={router} />);
  return router;
}

afterEach(() => { vi.useRealTimers(); cleanup(); handlers.length = 0; vi.restoreAllMocks(); });

describe("the Subtitle providers list follows a change made elsewhere (#1329)", () => {
  it("reads the list again when the subtitles settings change, and not for another area", async () => {
    const router = show();
    await screen.findByText("SubDL");
    const revalidate = vi.spyOn(router, "revalidate");
    const send = (name: string, payload: unknown) => act(() => { for (const handler of [...handlers]) handler(name, payload); });
    vi.useFakeTimers();

    send("ConfigChanged", { area: ConfigAreas.general });
    send("ConfigChanged", null);
    act(() => { vi.advanceTimersByTime(5_000); });
    expect(revalidate).not.toHaveBeenCalled();

    send("ConfigChanged", { area: ConfigAreas.subtitles });
    act(() => { vi.advanceTimersByTime(5_000); });
    expect(revalidate).toHaveBeenCalledTimes(1);
  });
});
