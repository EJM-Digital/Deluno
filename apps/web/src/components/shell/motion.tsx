/**
 * Wraps the app layout's `<Outlet />` so a route change replaces the page in place.
 * Page-local reveals are CSS (`.stagger`), which costs no JS.
 */

import { useLocation, useOutlet } from "react-router-dom";

/**
 * Wrap this around `<Outlet />` in the app layout. Route changes replace the content in place: exit/enter presence
 * kept the previous route mounted while the next one loaded, which made fast menu clicks show ghost cards and
 * partial frames underneath the new page.
 */
export function PageTransition() {
  const outlet = useOutlet();
  const location = useLocation();
  return <div key={location.pathname} className="min-h-[60vh]">{outlet}</div>;
}
