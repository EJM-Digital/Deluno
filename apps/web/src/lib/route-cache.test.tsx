import { render, screen, waitFor, act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClientProvider } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RouteSourceGate } from "../components/shell/route-source-gate";
import { defineSources, queryClient, sourceLoader, startSources, useRouteSources } from "./route-cache";
import { setRealtimeConnected } from "./realtime-state";

afterEach(() => {
  vi.useRealTimers();
});

/**
 * #1081: a screen opens on what the cache already holds and asks again behind
 * it. Nothing waits on the slowest request, and a source that fails says so in
 * place instead of taking the page to the error screen.
 */
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((res) => { resolve = res; });
  return { promise, resolve };
}

function makeSources(main: () => Promise<string>, extra: () => Promise<string[]>) {
  return defineSources({
    main: { key: ["t", "main"], label: "The main list", fetch: main },
    extra: { key: ["t", "extra"], label: "The extra list", fetch: extra, fallback: [] as string[] }
  });
}

function Probe({ sources }: { sources: ReturnType<typeof makeSources> }) {
  const state = useRouteSources(sources);
  return (
    <RouteSourceGate state={state}>
      {() => (
        <div>
          <p>main: {state.data.main}</p>
          <p>extra: {state.data.extra.join(",") || "none yet"}</p>
        </div>
      )}
    </RouteSourceGate>
  );
}

function renderProbe(sources: ReturnType<typeof makeSources>) {
  return render(
    <QueryClientProvider client={queryClient}>
      <Probe sources={sources} />
    </QueryClientProvider>
  );
}

describe("route sources", () => {
  beforeEach(() => queryClient.clear());

  it("the loader starts every source and returns without waiting for any", () => {
    const slow = vi.fn(() => new Promise<string>(() => {}));
    const sources = defineSources({
      a: { key: ["t", "a"], label: "A", fetch: slow },
      b: { key: ["t", "b"], label: "B", fetch: slow }
    });

    const result = sourceLoader(sources)();

    expect(result).toEqual({ cached: true });
    expect(slow).toHaveBeenCalledTimes(2);
  });

  it("joins a request already on its way instead of starting another", () => {
    const fetch = vi.fn(() => new Promise<string>(() => {}));
    const sources = defineSources({ a: { key: ["t", "join"], label: "A", fetch } });

    startSources(sources);
    startSources(sources);

    expect(fetch).toHaveBeenCalledTimes(1);
  });

  it("skips the sources that belong to another section of the screen", () => {
    const wanted = vi.fn(() => new Promise<string>(() => {}));
    const other = vi.fn(() => new Promise<string[]>(() => {}));
    const sources = defineSources({
      shared: { key: ["t", "shared"], label: "Shared", fetch: wanted },
      routing: { key: ["t", "routing"], label: "Routing", fetch: other, sections: ["routing"], fallback: [] as string[] }
    });

    startSources(sources, "indexers");

    expect(wanted).toHaveBeenCalledTimes(1);
    expect(other).not.toHaveBeenCalled();

    startSources(sources, "routing");
    expect(other).toHaveBeenCalledTimes(1);
  });

  it("draws as soon as what it needs has arrived, without waiting for the slow source", async () => {
    const extra = deferred<string[]>();
    renderProbe(makeSources(() => Promise.resolve("first"), () => extra.promise));

    expect(await screen.findByText("main: first")).toBeInTheDocument();
    expect(screen.getByText("extra: none yet")).toBeInTheDocument();

    await act(async () => extra.resolve(["x", "y"]));
    expect(await screen.findByText("extra: x,y")).toBeInTheDocument();
  });

  it("shows its own shape in place until the first answer, never a blank page", async () => {
    const main = deferred<string>();
    const { container } = renderProbe(makeSources(() => main.promise, () => Promise.resolve([])));

    expect(container.querySelector("[data-route-skeleton]")).not.toBeNull();

    await act(async () => main.resolve("late"));
    expect(await screen.findByText("main: late")).toBeInTheDocument();
    expect(container.querySelector("[data-route-skeleton]")).toBeNull();
  });

  it("on a revisit shows the last answer at once and refreshes it behind", async () => {
    const first = renderProbe(makeSources(() => Promise.resolve("old"), () => Promise.resolve(["a"])));
    await screen.findByText("main: old");
    first.unmount();

    const refreshed = deferred<string>();
    const main = vi.fn(() => refreshed.promise);
    // Outside the window in which an answer is shared, so the revisit asks again.
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(Date.now() + 3_100);
    renderProbe(makeSources(main, () => Promise.resolve(["a"])));

    // Drawn from the cache before the new answer exists.
    expect(screen.getByText("main: old")).toBeInTheDocument();
    await waitFor(() => expect(main).toHaveBeenCalled());

    await act(async () => refreshed.resolve("new"));
    expect(await screen.findByText("main: new")).toBeInTheDocument();
  }, 10_000);

  it("says which source failed, in place, and Retry reads it again", async () => {
    let calls = 0;
    const main = () => (++calls === 1 ? Promise.reject(new Error("The host said no.")) : Promise.resolve("recovered"));
    renderProbe(makeSources(main, () => Promise.resolve([])));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("The main list could not be read.");
    expect(alert).toHaveTextContent("The host said no.");

    await userEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(await screen.findByText("main: recovered")).toBeInTheDocument();
  });

  it("keeps drawing with the fallback and names a failed optional source, with a Retry", async () => {
    let calls = 0;
    const extra = () => (++calls === 1 ? Promise.reject(new Error("Nope.")) : Promise.resolve(["ok"]));
    renderProbe(makeSources(() => Promise.resolve("fine"), extra));

    expect(await screen.findByText("main: fine")).toBeInTheDocument();
    expect(await screen.findByRole("alert")).toHaveTextContent("The extra list could not be read.");
    expect(screen.getByText("extra: none yet")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Retry" }));
    expect(await screen.findByText("extra: ok")).toBeInTheDocument();
  });
});

/**
 * #1179: no screen reads its data on a timer. The server pushes every change and a
 * reconnect reads each screen once, so a source is read when it is opened and again
 * only when something invalidates it, whether or not the connection is up.
 */
describe("no polling timers", () => {
  function Quiet({ fetch }: { fetch: () => Promise<number> }) {
    const sources = defineSources({ n: { key: ["t", "quiet"], label: "Count", fetch } });
    const state = useRouteSources(sources);
    return <p>n: {String(state.data.n)}</p>;
  }

  // Fake timers: a timer the screen started would fire inside this advance, with no real waiting.
  const sleep = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms); });

  beforeEach(() => {
    vi.useFakeTimers();
    queryClient.clear();
    setRealtimeConnected(false);
  });

  it.each([true, false])("reads a source once and runs no timer with realtime up = %s", async (up) => {
    setRealtimeConnected(up);
    let calls = 0;
    const fetch = vi.fn(() => Promise.resolve(++calls));
    render(<QueryClientProvider client={queryClient}><Quiet fetch={fetch} /></QueryClientProvider>);

    await sleep(300);

    expect(fetch).toHaveBeenCalledTimes(1);
    setRealtimeConnected(false);
  });

  it("reads a source again when something invalidates it", async () => {
    let calls = 0;
    const fetch = vi.fn(() => Promise.resolve(++calls));
    render(<QueryClientProvider client={queryClient}><Quiet fetch={fetch} /></QueryClientProvider>);
    await sleep(50);
    expect(fetch).toHaveBeenCalledTimes(1);

    await act(async () => { await queryClient.invalidateQueries({ queryKey: ["t", "quiet"], exact: true }); });
    await sleep(50);

    expect(fetch).toHaveBeenCalledTimes(2);
  });
});
