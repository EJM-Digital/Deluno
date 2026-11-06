// Phase timing and deferred work for the gateway's routes (#1527).

/** Collects how long each phase took and renders a Server-Timing header. */
export function createTimings(now = () => performance.now()) {
  const phases = new Map();
  const add = (name, ms) => phases.set(name, (phases.get(name) ?? 0) + ms);
  return {
    async measure(name, work) {
      const started = now();
      try {
        return await work();
      } finally {
        add(name, now() - started);
      }
    },
    measureSync(name, work) {
      const started = now();
      try {
        return work();
      } finally {
        add(name, now() - started);
      }
    },
    header() {
      return [...phases].map(([name, ms]) => `${name};dur=${ms.toFixed(1)}`).join(", ");
    }
  };
}

/**
 * Lets a promise finish after the answer has gone. On the Workers runtime that is
 * ctx.waitUntil; with no context (tests, other hosts) the work is awaited by
 * nobody, but a rejection is swallowed so it can never fail the request.
 */
export function deferWork(ctx, promise) {
  const safe = Promise.resolve(promise).catch(() => {});
  if (ctx && typeof ctx.waitUntil === "function") ctx.waitUntil(safe);
  return safe;
}
