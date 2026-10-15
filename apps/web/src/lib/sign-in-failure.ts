import { describeRequestFailure, RequestFailedError } from "./search-reasons";

/**
 * What the sign-in and first-run screens say when the server refuses, in plain words and never the raw reply (#1339).
 * Everything but the two cases that only these screens have goes through the shared request-failure helper.
 */
export async function describeAuthRefusal(error: unknown, action: string): Promise<string> {
  if (error instanceof RequestFailedError && error.response.status === 401) {
    // The one 401 that is not "your session ended": this is the sign-in itself.
    return "That username or password is not right.";
  }

  // A refusal the server already worded for this screen: a field that is missing or wrong (400), or an install that
  // already has its account (409). Its own sentence beats a status code.
  if (error instanceof RequestFailedError && (error.response.status === 400 || error.response.status === 409)) {
    try {
      const body = (await error.response.clone().json()) as { errors?: Record<string, string[]>; message?: string };
      const sentence = body.message ?? Object.values(body.errors ?? {}).flat()[0];
      if (sentence) return sentence;
    } catch {
      // Fall through to the shared sentence.
    }
  }

  // A sentence the page or provider wrote itself (not a reply, and not the browser's "Failed to fetch").
  if (error instanceof Error && !(error instanceof TypeError) && !(error instanceof RequestFailedError) && error.message) {
    return error.message;
  }

  const explained = await describeRequestFailure(null, error, { action });
  return [explained.title, explained.description].filter(Boolean).join(". ").replace(/\.\./g, ".");
}
