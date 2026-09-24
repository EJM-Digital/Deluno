import { useId } from "react";

/**
 * Deluno's mark: an amber route swoosh and a "D", on a dark rounded-square
 * badge. The one definition, so the sidebar, the mobile topbar and the two
 * auth pages cannot draw three different things and call all of them the
 * Deluno logo (#637) — which is what happened: sign-in and account setup each
 * had their own ad-hoc TV-with-play-button glyph, unrelated to this mark, to
 * the favicon, and to the installed-app icon in manifest.json.
 *
 * <p>The gradient's id is per-instance (`useId`): the sidebar and the mobile
 * topbar both mount this at once, one hidden by CSS rather than unrendered,
 * so a shared literal id would leave two elements in the document answering
 * to the same id.</p>
 */
export function AppMark({ size = 30 }: { size?: number }) {
  const gradientId = useId();
  const markColor = "#FFD15A";
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 1024 1024"
      fill="none"
      aria-hidden
      style={{
        filter: "drop-shadow(0 2px 8px hsl(var(--primary)/0.28))",
        flexShrink: 0,
      }}
    >
      <rect width="1024" height="1024" rx="228" fill={`url(#${gradientId})`} />
      <rect x="76" y="76" width="872" height="872" rx="192" stroke={markColor} strokeOpacity="0.38" strokeWidth="20" />
      <path d="M256 650C197 520 255 363 397 294C563 213 754 289 823 461" stroke={markColor} strokeWidth="52" strokeLinecap="round" />
      <path d="M823 461L734 431" stroke={markColor} strokeWidth="52" strokeLinecap="round" />
      <path d="M823 461L787 375" stroke={markColor} strokeWidth="52" strokeLinecap="round" />
      <circle cx="256" cy="650" r="30" fill={markColor} />
      <text x="512" y="635" textAnchor="middle" fontFamily="Inter, Arial, sans-serif" fontSize="350" fontWeight="900" fill={markColor}>D</text>
      <defs>
        <radialGradient id={gradientId} cx="0" cy="0" r="1" gradientUnits="userSpaceOnUse" gradientTransform="translate(705 184) rotate(124) scale(836)">
          <stop offset="0%" stopColor="#2A2618" />
          <stop offset="44%" stopColor="#111821" />
          <stop offset="100%" stopColor="#070A0F" />
        </radialGradient>
      </defs>
    </svg>
  );
}
