/**
 * The one way the app writes a counted thing: "1 movie library", "2 movie libraries", "1,200 titles".
 * Never build a count by hand with `count === 1 ? "" : "s"`: that wrote "2 movie librarys" (#1423), and
 * `scripts/check-hand-built-plurals.mjs` fails the build on it.
 *
 * Without `many`, the word is pluralised the English way: a consonant + y becomes ies (library, entry), a word ending
 * in s, x, ch or sh takes es (dispatch, match), and everything else takes s. Give `many` for the rest
 * ("downloads have", "imports need", "person" -> "people").
 */
export function pluralWord(one: string): string {
  if (/[^aeiou]y$/i.test(one)) return `${one.slice(0, -1)}ies`;
  if (/(s|x|ch|sh)$/i.test(one)) return `${one}es`;
  return `${one}s`;
}

export function plural(count: number, one: string, many = pluralWord(one)): string {
  return `${count.toLocaleString("en-AU")} ${count === 1 ? one : many}`;
}

/** Only the word for a count, when the number is drawn apart from it (a bold figure, then " peers"). */
export function pluralNoun(count: number, one: string, many = pluralWord(one)): string {
  return count === 1 ? one : many;
}
