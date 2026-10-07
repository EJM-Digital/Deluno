/**
 * Finds help text a page says more than once (#1162).
 *
 * A sentence of 50 or more characters that appears twice in a page's visible
 * text is almost always one description pasted into several sections or
 * tables. List rows are real data (two downloads can share a long release
 * name), so anything inside a row is left out.
 */
export function repeatedSentences(root: HTMLElement, minLength = 50): string[] {
  const seen = new Map<string, number>();
  const walker = root.ownerDocument.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
  for (let node = walker.currentNode as Element | null; node; node = walker.nextNode() as Element | null) {
    if (node.closest('[role="row"]')) continue;
    const own = Array.from(node.childNodes)
      .filter((child) => child.nodeType === Node.TEXT_NODE)
      .map((child) => child.textContent ?? "")
      .join(" ");
    for (const sentence of own.split(/(?<=[.!?])\s+/)) {
      const text = sentence.replace(/\s+/g, " ").trim();
      if (text.length >= minLength) seen.set(text, (seen.get(text) ?? 0) + 1);
    }
  }
  return [...seen].filter(([, count]) => count > 1).map(([text]) => text);
}
