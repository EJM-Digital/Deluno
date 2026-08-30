/**
 * The artwork behind a detail page's header, and the scrim that keeps the text
 * on top of it readable.
 *
 * <p>The scrim used to be a constant: two stacked full-coverage gradients over
 * the image at 0.34. That is two bugs in one. Stacked, they multiplied the
 * backdrop down to roughly a tenth of itself — James: <i>"we have also lost the
 * background silhouette image on the main hero"</i>. And being constant, one
 * setting had to serve every plate: Arrival's backdrop is a pale fog wall and
 * needs a heavy scrim to hold light type, while a dark night-time plate is
 * ruined by the same amount. James again: <i>"backdrop is good but it needs to
 * be dynamic, cause some of the text in arrival is hard to read now but on other
 * titles its OK"</i>.</p>
 *
 * <p>So it is solved rather than chosen. The image is drawn once to a tiny
 * canvas and the mean luminance of the band the text actually sits over is
 * measured; the scrim is set from that. A bright plate gets a heavy scrim, a
 * dark one gets almost none, and the artwork is as visible as it can be while
 * the words on top of it stay legible.</p>
 */
import { useEffect, useRef, useState } from "react";

interface HeroBackdropProps {
  url?: string | null;
}

/**
 * How opaque the text-side scrim is before anything has been measured.
 *
 * <p>Deliberately the safe end: an unmeasured plate is assumed bright, because
 * a scrim that is too heavy costs some artwork and one that is too light costs
 * the text. Cross-origin artwork with no CORS header, a decode failure, or a
 * browser with no canvas all land here and all stay readable.</p>
 */
const FALLBACK_SCRIM = 0.82;

/** Only the left of the header carries type, so only the left is measured. */
const TEXT_BAND_FRACTION = 0.55;

export function HeroBackdrop({ url }: HeroBackdropProps) {
  const scrim = useBackdropScrim(url);

  return (
    <>
      {url ? (
        <img
          src={url}
          alt=""
          className="pointer-events-none absolute inset-0 h-full w-full scale-105 object-cover object-top saturate-[0.85]"
          // Solved, so it is an inline value rather than a class: there is no
          // useful set of Tailwind steps between "dark plate" and "fog wall".
          style={{ opacity: 0.42 + (1 - scrim) * 0.38 }}
        />
      ) : null}
      {/*
        One scrim, clearing to nothing by the right-hand edge — the second
        full-coverage gradient is what made the first one invisible.
      */}
      <div
        className="pointer-events-none absolute inset-0"
        style={{
          backgroundImage:
            `linear-gradient(to right, hsl(var(--card)) 0%, hsl(var(--card) / ${scrim.toFixed(2)}) 45%, transparent 100%)`
        }}
      />
      {/* A band, not a wash: it blends the card's bottom edge and nothing more. */}
      <div className="pointer-events-none absolute inset-x-0 bottom-0 h-24 bg-gradient-to-t from-card to-transparent" />
    </>
  );
}

/**
 * Mean luminance of the backdrop's text band, as a scrim opacity between a
 * light touch and near-solid.
 */
function useBackdropScrim(url?: string | null): number {
  const [scrim, setScrim] = useState(FALLBACK_SCRIM);
  // Survives an unmount mid-decode, which a slow plate on a fast navigation
  // will do every time.
  const live = useRef(true);

  useEffect(() => {
    live.current = true;
    if (!url) {
      setScrim(FALLBACK_SCRIM);
      return () => { live.current = false; };
    }

    const image = new Image();
    image.crossOrigin = "anonymous";
    image.onload = () => {
      if (!live.current) return;
      const luminance = measureLuminance(image);
      // Bright plate → heavy scrim; dark plate → light one. Clamped so a black
      // backdrop still gets enough to seat the type, and a white one does not
      // go fully solid and hide the artwork altogether.
      setScrim(luminance === null ? FALLBACK_SCRIM : clamp(0.34 + luminance * 0.72, 0.4, 0.92));
    };
    image.onerror = () => { if (live.current) setScrim(FALLBACK_SCRIM); };
    image.src = url;

    return () => { live.current = false; };
  }, [url]);

  return scrim;
}

/**
 * Perceived brightness of the left band, 0..1, or null if the pixels cannot be
 * read — a tainted canvas, or no canvas at all.
 */
function measureLuminance(image: HTMLImageElement): number | null {
  try {
    // 24×12 is enough for a mean and cheap enough to be unnoticeable; the
    // browser's own downscale does the averaging.
    const width = 24;
    const height = 12;
    const canvas = document.createElement("canvas");
    canvas.width = width;
    canvas.height = height;

    const context = canvas.getContext("2d", { willReadFrequently: true });
    if (!context) return null;

    context.drawImage(image, 0, 0, width, height);
    const band = Math.max(1, Math.round(width * TEXT_BAND_FRACTION));
    const { data } = context.getImageData(0, 0, band, height);

    let total = 0;
    for (let index = 0; index < data.length; index += 4) {
      // Rec. 709 luma: green carries most of what an eye reads as brightness,
      // so a flat RGB average calls a green plate darker than it looks.
      total += (0.2126 * data[index] + 0.7152 * data[index + 1] + 0.0722 * data[index + 2]) / 255;
    }

    return total / (data.length / 4);
  } catch {
    // A tainted canvas throws on getImageData. That is not an error worth
    // reporting — it is the fallback path, and the fallback is readable.
    return null;
  }
}

function clamp(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}
