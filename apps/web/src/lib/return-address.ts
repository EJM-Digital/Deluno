/**
 * Where sign-in and setup may send the person afterwards.
 *
 * A `return` address arrives in a link, so it is whatever the sender typed. It is followed only when it is a path on
 * this app: one leading "/", then not another "/" or a "\" (browsers read both as "another site"), no scheme, and no
 * control characters (a tab or newline inside "/\t/host" is dropped by the URL parser and leaves "//host"). The
 * address is also checked after one round of percent-decoding, so "/%5Chost" is refused the way "/\host" is.
 * Every place that reads a return address goes through here (#1339).
 */
export function safeReturnAddress(value: string | null | undefined, fallback = "/"): string {
  if (typeof value !== "string" || value.length === 0) return fallback;
  if (!isPathOnThisApp(value)) return fallback;

  let decoded: string;
  try {
    decoded = decodeURIComponent(value);
  } catch {
    return fallback;
  }
  if (!isPathOnThisApp(decoded)) return fallback;

  // The last word is the URL parser itself: whatever it makes of the address must still be this app.
  try {
    const base = "http://deluno.invalid";
    if (new URL(value, base).origin !== base) return fallback;
  } catch {
    return fallback;
  }

  return value;
}

function isPathOnThisApp(value: string): boolean {
  if (value[0] !== "/") return false;
  if (value[1] === "/" || value[1] === "\\") return false;
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001f\u007f]/.test(value)) return false;
  // A backslash in the path part is a slash to a browser, so "/a\..." could still become another site's path.
  const pathPart = value.split(/[?#]/, 1)[0];
  return !pathPart.includes("\\");
}
