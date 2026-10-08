/** A downloads folder is optional, but when given it must be a full path (C:\..., \\server\share, or /...). */
export function downloadsPathProblem(value: string): string | null {
  const path = value.trim();
  if (!path) return null;
  return /^([a-zA-Z]:[\\/]|\\\\|\/)/.test(path)
    ? null
    : "Write the downloads folder as a full path, such as C:\\Downloads\\Completed or \\\\nas\\downloads.";
}

/**
 * A folder spelled so two spellings of it compare equal: both separators alike, no trailing slash,
 * and lower case when it is a Windows path (a drive letter or a share), which ignores case. A Linux
 * path keeps its case. qBittorrent reports `C:/Downloads/...` where the others say `C:\Downloads\...`.
 */
export function comparablePath(value: string | null | undefined): string {
  const path = (value ?? "").trim().replace(/\\/g, "/").replace(/\/{2,}/g, (run, offset) => (offset === 0 ? "//" : "/")).replace(/\/+$/, "");
  return /^([a-zA-Z]:|\/\/)/.test(path) ? path.toLowerCase() : path;
}

/** True when two folders are the same folder, however each was spelled. */
export function samePath(left: string | null | undefined, right: string | null | undefined): boolean {
  const a = comparablePath(left);
  return a !== "" && a === comparablePath(right);
}
