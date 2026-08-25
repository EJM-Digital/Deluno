const LOWERCASE_CONNECTORS = new Set(["a", "an", "and", "as", "at", "by", "for", "from", "in", "of", "on", "or", "the", "to", "with"]);

/**
 * Applies the app's display-title convention without damaging acronyms such as
 * TV, API, IMDb, or 4K. Sentence copy should not use this helper.
 */
export function titleCaseLabel(value: string): string {
  const words = value.split(/(\s+)/);
  let wordIndex = -1;
  const wordCount = words.filter((word) => !/^\s+$/.test(word)).length;

  return words
    .map((word) => {
      if (/^\s+$/.test(word)) return word;
      wordIndex += 1;
      const lower = word.toLowerCase();
      const isConnector = wordIndex > 0 && wordIndex < wordCount - 1 && LOWERCASE_CONNECTORS.has(lower);
      if (isConnector) return lower;
      if (/^[A-Z0-9][A-Z0-9&./+-]*$/.test(word)) return word;
      return word.charAt(0).toUpperCase() + word.slice(1);
    })
    .join("");
}
