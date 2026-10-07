import type { ReactNode } from "react";
import { Link } from "react-router-dom";

/** Where the one Cloudflare helper for every tracker is set (#1143). */
export const CLOUDFLARE_HELPER_PLACE = "Preferences, General";

/** A link to the page, by the name the screen gives it. */
export function PreferencesLink({ children = "Open Preferences, General" }: { children?: ReactNode }) {
  return <Link to="/settings/general" className="font-medium text-primary hover:underline">{children}</Link>;
}

/** Whether a message sends the person to Preferences, General. */
export function mentionsCloudflare(text: string | null | undefined): boolean {
  return Boolean(text && /cloudflare/i.test(text));
}

/** The message, with a link to Preferences, General after it when it names that page. */
export function MessageWithPreferencesLink({ text }: { text: string }) {
  return text.includes(CLOUDFLARE_HELPER_PLACE) ? <>{text} <PreferencesLink /></> : <>{text}</>;
}
