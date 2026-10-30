import type { ReactNode } from "react";
import { ArtworkImage } from "../ui/artwork-image";

/**
 * The eyebrow, the title (or its logo) and the monitoring shield that head a
 * film's and a show's page (#1441). Both pages draw it here so they cannot
 * drift apart again.
 *
 * With a TMDb logo (#864) the logo IS the heading: its alt text is the title,
 * and the title is not written a second time (if the logo fails to load the
 * text title takes its place). Centre, not baseline, lines the heading up with
 * the shield.
 */
export function DetailTitleRow({
  eyebrow,
  title,
  logoUrl,
  shield
}: {
  eyebrow: string;
  title: string;
  logoUrl: string | null | undefined;
  shield: ReactNode;
}) {
  return (
    <>
      <p className="text-(length:--section-eyebrow-size) font-bold uppercase tracking-[0.18em] text-primary">{eyebrow}</p>
      <div className="mt-1 flex flex-wrap items-center gap-x-3 gap-y-1 xl:flex-nowrap">
        <h1 className="min-w-0 font-display text-4xl font-semibold tracking-tight text-foreground sm:text-5xl xl:truncate xl:text-3xl" title={title}>
          {logoUrl ? (
            <ArtworkImage
              src={logoUrl}
              alt={title}
              fallback={title}
              className="block h-12 w-auto max-w-[18rem] object-contain object-left"
            />
          ) : title}
        </h1>
        {shield}
      </div>
    </>
  );
}
