// Provider-call limits. Only a call that really leaves for TMDb or TheTVDB is
// counted; an answer served from the gateway's own cache costs them nothing and
// is never limited. The counts live in Cloudflare's Workers Rate Limiting
// bindings, so nothing here writes to KV. A binding that is missing (local
// tests, an older deployment) allows the call.

export class ProviderRateLimited extends Error {
  constructor(retryAfterSeconds) {
    super("Provider call budget spent");
    this.retryAfterSeconds = retryAfterSeconds;
  }
}

async function tryLimit(binding, key) {
  if (!binding || typeof binding.limit !== "function") {
    return true;
  }
  try {
    return (await binding.limit({ key })).success !== false;
  } catch {
    // A limiter that errors must never take the gateway down.
    return true;
  }
}

/** One install's budget first (120 a minute), then the shared ceiling (400 every 10 seconds). */
export async function spendProviderCall(env, clientAddress) {
  if (!(await tryLimit(env.PROVIDER_CALLS_PER_INSTALL, clientAddress))) {
    return { allowed: false, retryAfterSeconds: 60 };
  }
  if (!(await tryLimit(env.PROVIDER_CALLS_SHARED, "provider"))) {
    return { allowed: false, retryAfterSeconds: 10 };
  }
  return { allowed: true, retryAfterSeconds: 0 };
}

/** A fetch that spends one provider call before every upstream request. */
export function limitedFetch(env, clientAddress, upstream = fetch) {
  return async (url, init) => {
    const spend = await spendProviderCall(env, clientAddress);
    if (!spend.allowed) {
      throw new ProviderRateLimited(spend.retryAfterSeconds);
    }
    return upstream(url, init);
  };
}

export function rateLimitedResponse(error, json) {
  return json(
    { error: "rate_limited", message: "Please try title matching again shortly." },
    429,
    { "Retry-After": String(error.retryAfterSeconds) });
}
