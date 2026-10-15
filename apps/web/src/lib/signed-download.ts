import { authedFetch } from "./use-auth";
import { readErrorText } from "./api";

/** The file name the server offered in Content-Disposition, if any. */
export function fileNameFromDisposition(header: string | null): string | null {
  if (!header) return null;
  const star = /filename\*\s*=\s*(?:UTF-8'')?([^;]+)/i.exec(header);
  if (star) {
    try {
      return decodeURIComponent(star[1].trim().replace(/^"|"$/g, ""));
    } catch {
      /* fall through to the plain form */
    }
  }
  const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(header);
  return plain ? plain[1].trim() : null;
}

/**
 * Fetches an /api/ route with the person's sign-in (a followed link sends none, so the server answers 401)
 * and either saves it as a file or opens it in a new tab. Throws with the server's reason when refused (#1331).
 */
export async function fetchSignedFile(path: string, mode: "save" | "open", fallbackName: string): Promise<void> {
  const response = await authedFetch(path);
  if (!response.ok) throw new Error(await readErrorText(response, "That could not be downloaded."));
  const received = await response.blob();
  // A text page opens as readable text in the tab instead of being offered as a download.
  const blob = mode === "open" && received.type.startsWith("text/") ? new Blob([received], { type: "text/plain;charset=utf-8" }) : received;
  const url = URL.createObjectURL(blob);
  if (mode === "open") {
    window.open(url, "_blank", "noopener");
    window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
    return;
  }
  const link = document.createElement("a");
  link.href = url;
  link.download = fileNameFromDisposition(response.headers.get("content-disposition")) ?? fallbackName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
}
