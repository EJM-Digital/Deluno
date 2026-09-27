/**
 * Dedicated worker for the shared live ticker (#937).
 *
 * A page that is hidden, occluded behind another window, or minimised has
 * its own chained `setInterval`/`setTimeout` timers throttled by Chrome:
 * after roughly five minutes they run at most once a minute
 * (https://developer.chrome.com/blog/background_tabs/). A dedicated worker
 * is a separate execution context that policy does not reach, so a timer
 * running in here keeps its real rate the whole time Deluno sits in the
 * background. See ../lib/live-ticker.ts for the main-thread side that
 * starts this worker and fans its ticks out to every subscriber.
 */
export {};

interface StartMessage {
  type: "start";
  intervalMs: number;
}

interface StopMessage {
  type: "stop";
}

type InboundMessage = StartMessage | StopMessage;

// Narrowly typed by hand rather than via the "webworker" lib: this project's
// tsconfig targets "DOM" for the app's own main-thread code, and "webworker"
// declares its own incompatible `self` (DedicatedWorkerGlobalScope vs.
// Window). Casting through `unknown` keeps this one file self-contained
// without a second tsconfig project just for it.
const scope = self as unknown as {
  postMessage(message: { type: "tick"; at: number }): void;
  onmessage: ((event: MessageEvent<InboundMessage>) => void) | null;
};

let timer: ReturnType<typeof setInterval> | undefined;

scope.onmessage = (event) => {
  const message = event.data;
  if (message.type === "start") {
    if (timer !== undefined) clearInterval(timer);
    timer = setInterval(() => scope.postMessage({ type: "tick", at: Date.now() }), message.intervalMs);
  } else if (message.type === "stop") {
    if (timer !== undefined) clearInterval(timer);
    timer = undefined;
  }
};
