/** A downloads folder is optional, but when given it must be a full path (C:\..., \\server\share, or /...). */
export function downloadsPathProblem(value: string): string | null {
  const path = value.trim();
  if (!path) return null;
  return /^([a-zA-Z]:[\\/]|\\\\|\/)/.test(path)
    ? null
    : "Write the downloads folder as a full path, such as C:\\Downloads\\Completed or \\\\nas\\downloads.";
}
