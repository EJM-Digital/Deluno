/**
 * The tracker definitions' notes carry a little HTML; the words are what matter.
 * Tags are removed until none is left, so a tag split around another one
 * ("<scr<b>ipt>") cannot be put back together by a single pass.
 */
export function plainText(html: string): string {
  let text = html.replace(/<br\s*\/?>/gi, " ");
  for (let previous = ""; previous !== text; ) {
    previous = text;
    text = text.replace(/<[^>]*>/g, "");
  }
  // A lone "<" that never closed is text, not a tag; drop it so nothing can start one later.
  text = text.replace(/[<>]/g, "");
  return text.replace(/&amp;/g, "&").replace(/\s+/g, " ").trim();
}
