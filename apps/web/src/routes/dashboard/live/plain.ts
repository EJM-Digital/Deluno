/**
 * Why a download is stuck or a hand-off failed, in plain words (#1096). The
 * client and the processor give their own technical sentences ("The processor
 * refused the hand-off with HTTP 400: The hand-off names '...' which ..."); a
 * card says what it means in a short sentence, and the original is kept as its
 * tooltip. Anything not recognised is left exactly as it was said.
 */

export interface PlainReason {
  /** The short sentence a card shows. */
  text: string;
  /** What was originally said, for the tooltip. */
  full: string;
}

const RULES: Array<[RegExp, (match: RegExpExecArray) => string]> = [
  [/refused the hand-?off with HTTP (\d{3})/i, (match) => {
    const status = Number(match[1]);
    if (status === 401 || status === 403) return "The processor refused Deluno's key, so it was not cleaned.";
    if (status === 404) return "The processor could not find it, so it was not cleaned.";
    if (status >= 500) return "The processor hit an error, so it was not cleaned.";
    return "The processor refused the hand-off.";
  }],
  [/timed out|took too long/i, () => "The processor took too long and Deluno stopped waiting."],
  [/unreachable|could not be reached|connection refused|no connection|could not connect/i, () => "Deluno could not reach the processor."],
  [/(output|cleaned (?:file|folder)s?|result).*(missing|not found|does not exist|never appeared|was not there)/i, () => "The processor finished but its cleaned files are not there."],
  [/missing or invalid torrent data/i, () => "Torrent data missing or broken."],
  [/no longer lists (?:this|the) download|no longer has (?:this|the) download/i, () => "The download client no longer has it."],
  [/stopped moving/i, () => "It stopped moving in the download client."],
  [/no one is sharing|no peers|no seeds|nobody is sharing/i, () => "Nobody is sharing it."]
];

export function plainReason(said: string | null | undefined): PlainReason | null {
  const full = said?.trim();
  if (!full) return null;
  for (const [pattern, words] of RULES) {
    const match = pattern.exec(full);
    if (match) return { text: words(match), full };
  }
  return { text: full, full };
}
