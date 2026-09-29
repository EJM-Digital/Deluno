// Cache helpers shared by the gateway's routes (#1064).
//
//  - KV keys are limited to 512 bytes; a long non-Latin query percent-encodes to
//    three times its length, so any key over a safe length is replaced by a
//    SHA-256 of itself.
//  - An upstream failure is remembered for a short while per key, so a caller
//    that rotates addresses to get round the per-IP limiter cannot send an
//    unlimited number of distinct bad ids or queries on to the provider and
//    burn its quota. KV's smallest expirationTtl is 60 seconds.
//  - A KV read or write that fails is never allowed to fail the request.

export const MAX_KEY_LENGTH = 200;
export const ERROR_CACHE_SECONDS = 60;

const encoder = new TextEncoder();

export async function boundCacheKey(key) {
  if (key.length <= MAX_KEY_LENGTH) {
    return key;
  }
  const digest = await crypto.subtle.digest("SHA-256", encoder.encode(key));
  const hex = [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
  // A short readable head (the version and kind) keeps the keys tellable apart.
  return `${key.slice(0, 24)}~sha256:${hex}`;
}

export async function readCache(cache, key) {
  try {
    return await cache.get(key, "json");
  } catch {
    return null;
  }
}

export async function writeCache(cache, key, value, ttl) {
  try {
    await cache.put(key, typeof value === "string" ? value : JSON.stringify(value), { expirationTtl: ttl });
  } catch {
    // A cache that will not take a write only costs the next request a fresh lookup.
  }
}

const errorKey = (key) => `err:v1:${key}`;

/** A remembered failure for this key: { status, body, headers }, or null. */
export async function readCachedError(cache, key) {
  const cached = await readCache(cache, errorKey(key));
  return cached && Number.isInteger(cached.status) && cached.body ? cached : null;
}

export async function cacheError(cache, key, failure) {
  await writeCache(cache, errorKey(key), failure, ERROR_CACHE_SECONDS);
}
