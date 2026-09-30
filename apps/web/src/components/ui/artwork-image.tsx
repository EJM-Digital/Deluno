import { useEffect, useState, type ImgHTMLAttributes, type ReactNode } from "react";
import { cn } from "../../lib/utils";

type ArtworkImageProps = Omit<ImgHTMLAttributes<HTMLImageElement>, "src"> & {
  src: string | null | undefined;
  /**
   * What to show when there is no address or the image fails to load. Defaults
   * to Deluno's plain placeholder; pass `null` for decorative art (a backdrop)
   * that should simply disappear.
   */
  fallback?: ReactNode;
};

/**
 * The one place a poster, backdrop, screenshot or cast portrait is drawn, so a
 * missing or broken file shows Deluno's placeholder and never the browser's
 * broken-image icon. The address is tried again if it changes.
 */
export function ArtworkImage({ src, alt = "", className, fallback, loading, ...rest }: ArtworkImageProps) {
  const [failedSrc, setFailedSrc] = useState<string | null>(null);

  useEffect(() => {
    setFailedSrc(null);
  }, [src]);

  if (!src || failedSrc === src) {
    if (fallback !== undefined) {
      return <>{fallback}</>;
    }
    return <ArtworkPlaceholder title={alt} className={className} />;
  }

  return (
    <img
      {...rest}
      src={src}
      alt={alt}
      loading={loading}
      className={className}
      onError={(event) => {
        setFailedSrc(src);
        rest.onError?.(event);
      }}
    />
  );
}

export function ArtworkPlaceholder({ title, className, compact = false }: { title: string; className?: string; compact?: boolean }) {
  return (
    <div
      className={cn(
        "flex items-center justify-center bg-gradient-to-br from-surface-2 to-surface-3 text-center text-muted-foreground",
        className
      )}
      role={title ? "img" : undefined}
      aria-label={title ? `${title} artwork unavailable` : undefined}
      data-artwork-placeholder=""
    >
      <span className={cn("px-2 font-display font-semibold tracking-tight", compact ? "text-[length:var(--type-micro)]" : "text-sm")}>
        {title ? title.slice(0, 2).toUpperCase() : ""}
      </span>
    </div>
  );
}
