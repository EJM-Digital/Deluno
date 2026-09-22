/**
 * What an import job is about, from the payload whoever queued it wrote.
 *
 * <p>Read without regard to case, because the two writers disagree: an import
 * queued from a screen writes camelCase, and one queued from processor output
 * writes PascalCase. Reading only the first left every import of a processed
 * file named "Job 01a0c89f", with "Destination not resolved yet · Source path
 * not recorded" beside "Imported and named" (#564).</p>
 */
export interface ImportJobInfo {
  title: string | null;
  fileName: string | null;
  sourcePath: string | null;
  destinationPath: string | null;
  /** The download this import is for, which knows where the file landed. */
  dispatchId: string | null;
}

export function parseImportJobPayload(payloadJson: string | null): ImportJobInfo | null {
  if (!payloadJson) return null;
  try {
    const parsed = JSON.parse(payloadJson) as Record<string, unknown>;
    const preview = field(parsed, "preview");
    const read = (name: string) =>
      text(preview && typeof preview === "object" ? field(preview as Record<string, unknown>, name) : undefined) ??
      text(field(parsed, name));
    return {
      title: read("title"),
      fileName: read("fileName"),
      sourcePath: read("sourcePath"),
      destinationPath: read("destinationPath"),
      dispatchId: read("dispatchId")
    };
  } catch {
    return null;
  }
}

function field(source: Record<string, unknown>, name: string): unknown {
  const key = Object.keys(source).find((candidate) => candidate.toLowerCase() === name.toLowerCase());
  return key === undefined ? undefined : source[key];
}

function text(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}
