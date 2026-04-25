import { useEffect, useRef, useState } from "react";

interface LiveWaveformProps {
  /** Seed series for the waveform — updated on interval */
  seed: number[];
  /** Max value for normalization (in MB/s) */
  maxMbps?: number;
  label?: string;
  subLabel?: string;
}

/**
 * Live streaming-bar waveform: each frame pushes a new value and rolls off the oldest.
 * Renders gradient bars with a glow baseline — like a terminal network monitor.
 */
export function LiveWaveform({ seed, maxMbps = 60, label, subLabel }: LiveWaveformProps) {
  const [series, setSeries] = useState<number[]>(() => normalize(seed, 60));
  const tickRef = useRef(0);

  useEffect(() => {
    const id = window.setInterval(() => {
      setSeries((prev) => {
        tickRef.current += 1;
        const last = prev[prev.length - 1] ?? 20;
        // Random walk with gentle drift toward midline
        const drift = (30 - last) * 0.08;
        const noise = (Math.random() - 0.5) * 10;
        const next = Math.max(4, Math.min(maxMbps, last + drift + noise));
        return [...prev.slice(1), next];
      });
    }, 600);
    return () => window.clearInterval(id);
  }, [maxMbps]);

  const current = series[series.length - 1] ?? 0;
  const avg = series.reduce((a, b) => a + b, 0) / series.length;
  const peak = Math.max(...series);

  return (
    <div className="flex h-full flex-col">
      {label ? (
        <div className="mb-2 flex items-end justify-between gap-3">
          <div>
            <p className="text-[10px] font-bold uppercase tracking-[0.18em] text-muted-foreground/70">
              {label}
            </p>
            {subLabel ? (
              <p className="text-[11px] text-muted-foreground">{subLabel}</p>
            ) : null}
          </div>
          <div className="text-right">
            <p className="tabular text-2xl font-bold tracking-display text-foreground">
              {current.toFixed(1)}
              <span className="ml-1 text-xs font-medium text-muted-foreground">MB/s</span>
            </p>
            <p className="tabular text-[10px] text-muted-foreground">
              avg {avg.toFixed(1)} · peak {peak.toFixed(1)}
            </p>
          </div>
        </div>
      ) : null}

      <div className="relative flex flex-1 items-end gap-[3px]">
        {series.map((v, i) => {
          const h = (v / maxMbps) * 100;
          const isLast = i === series.length - 1;
          return (
            <div
              key={i}
              className="relative flex-1 rounded-full transition-all duration-500 ease-out"
              style={{
                height: `${Math.max(6, h)}%`,
                background: isLast
                  ? "linear-gradient(to top, hsl(var(--primary) / 0.9), hsl(var(--primary-glow)))"
                  : `linear-gradient(to top, hsl(var(--primary) / ${0.15 + (i / series.length) * 0.5}), hsl(var(--primary) / ${0.3 + (i / series.length) * 0.6}))`,
                boxShadow: isLast
                  ? "0 0 12px hsl(var(--primary) / 0.6)"
                  : undefined
              }}
            />
          );
        })}
      </div>
    </div>
  );
}

function normalize(seed: number[], targetLen: number): number[] {
  if (seed.length >= targetLen) return seed.slice(-targetLen);
  const out = [...seed];
  while (out.length < targetLen) {
    out.unshift(seed[0] ?? 20);
  }
  return out;
}
