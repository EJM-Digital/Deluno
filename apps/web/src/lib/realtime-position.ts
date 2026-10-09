/**
 * Where the realtime stream stood when this page first read its data (#1191).
 *
 * Every API answer carries the sequence of the newest event at the moment the request arrived (X-Deluno-Event-Seq). The
 * first one a page sees is kept. When the page's connection opens it asks the hub to replay what was published since,
 * so a change made between the first read and the first join of a subject is delivered instead of waiting for the next
 * change. Later answers are ignored: what matters is how old the oldest data on screen is.
 */
export const REALTIME_POSITION_HEADER = "X-Deluno-Event-Seq";

let firstPosition = 0;

export function noteRealtimePosition(response: Pick<Response, "headers">) {
  if (firstPosition > 0) return;
  const value = Number(response.headers.get(REALTIME_POSITION_HEADER));
  if (Number.isFinite(value) && value > 0) firstPosition = Math.floor(value);
}

/** The sequence at the page's first read, or 0 when no answer has carried one. */
export function firstRealtimePosition() {
  return firstPosition;
}

export function forgetRealtimePosition() {
  firstPosition = 0;
}
