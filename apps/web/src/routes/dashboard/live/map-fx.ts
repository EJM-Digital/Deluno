/**
 * The moving dots of the Sources map (#1098). One small engine draws every dot into a single SVG group: a question flying from
 * Deluno to an indexer, its answer flying back, a pulse to a connection, and the stream of particles that comes in from a client
 * while it downloads. Everything it draws comes from a real event or a real reading; nothing loops for decoration.
 *
 * It costs nothing at rest: the animation frame runs only while there are dots or a client is moving, stops when the tab is
 * hidden (a dot that is overdue when the tab comes back is dropped, not replayed), and draws nothing when motion is reduced.
 */
import { motionAllowed } from "../../../lib/page-motion";
import { flowOf, pointOn, type Curve } from "./map-model";

export type DotTone = "ok" | "warn" | "bad" | "ask" | "work";

const COLOR: Record<DotTone, string> = {
  ok: "hsl(var(--success))",
  warn: "hsl(var(--warning))",
  bad: "hsl(var(--destructive))",
  ask: "hsl(var(--info))",
  work: "hsl(var(--stage-process))"
};

const SVG_NS = "http://www.w3.org/2000/svg";

interface Dot {
  curve: Curve;
  reverse: boolean;
  startedAt: number;
  durationMs: number;
  radius: number;
  halo: SVGCircleElement;
  core: SVGCircleElement;
}

interface Stream {
  curve: Curve;
  mbps: number;
  carry: number;
}

export class MapFx {
  private layer: SVGGElement | null = null;
  private curves = new Map<string, Curve>();
  private dots: Dot[] = [];
  private streams = new Map<string, Stream>();
  private frame = 0;
  private last = 0;
  private onBusy: ((busy: boolean) => void) | null = null;
  private busy = false;
  private visibility = () => {
    if (document.visibilityState !== "visible") this.clear();
    else this.kick();
  };

  attach(layer: SVGGElement | null, onBusy: (busy: boolean) => void) {
    this.layer = layer;
    this.onBusy = onBusy;
    if (typeof document !== "undefined") document.addEventListener("visibilitychange", this.visibility);
  }

  detach() {
    if (typeof document !== "undefined") document.removeEventListener("visibilitychange", this.visibility);
    this.clear();
    this.layer = null;
    this.onBusy = null;
  }

  /** The current line from Deluno to every node, by node key. */
  setCurves(curves: ReadonlyMap<string, Curve>) {
    this.curves = new Map(curves);
    for (const [key, stream] of this.streams) {
      const curve = curves.get(key);
      if (curve) stream.curve = curve;
    }
  }

  /** A dot flies along a node's line: `out` from Deluno to the node, `back` from the node to Deluno. */
  trip(key: string, direction: "out" | "back", tone: DotTone, delayMs = 0, durationMs = 750) {
    const curve = this.curves.get(key);
    if (!curve || !this.layer || !motionAllowed()) return;
    this.add(curve, direction === "back", tone, performance.now() + delayMs, durationMs, 4);
    this.kick();
  }

  /** A client's speed: while it is above zero, particles stream in from it, faster and thicker as it speeds up. */
  setFlow(key: string, mbps: number) {
    const curve = this.curves.get(key);
    if (!(mbps > 0) || !curve) {
      this.streams.delete(key);
      return;
    }
    const known = this.streams.get(key);
    if (known) {
      known.mbps = mbps;
      known.curve = curve;
    } else this.streams.set(key, { curve, mbps, carry: 0 });
    this.kick();
  }

  private add(curve: Curve, reverse: boolean, tone: DotTone, startedAt: number, durationMs: number, radius: number) {
    if (!this.layer) return;
    const color = COLOR[tone];
    const halo = document.createElementNS(SVG_NS, "circle");
    const core = document.createElementNS(SVG_NS, "circle");
    halo.style.fill = color;
    halo.style.opacity = "0";
    core.style.fill = color;
    core.style.opacity = "0";
    core.setAttribute("r", String(radius));
    halo.setAttribute("r", String(radius * 2.6));
    this.layer.append(halo, core);
    this.dots.push({ curve, reverse, startedAt, durationMs, radius, halo, core });
  }

  private clear() {
    for (const dot of this.dots) {
      dot.halo.remove();
      dot.core.remove();
    }
    this.dots = [];
    cancelAnimationFrame(this.frame);
    this.frame = 0;
    this.last = 0;
    this.report(false);
  }

  private report(busy: boolean) {
    if (busy === this.busy) return;
    this.busy = busy;
    this.onBusy?.(busy);
  }

  private kick() {
    if (this.frame !== 0 || !this.layer || !motionAllowed()) return;
    this.frame = requestAnimationFrame(this.tick);
  }

  private tick = (now: number) => {
    this.frame = 0;
    if (!this.layer || !motionAllowed()) {
      this.clear();
      return;
    }
    const dt = this.last === 0 ? 16 : Math.min(100, now - this.last);
    this.last = now;
    for (const stream of this.streams.values()) {
      const { perSecond, travelMs } = flowOf(stream.mbps);
      stream.carry += (perSecond * dt) / 1000;
      while (stream.carry >= 1) {
        stream.carry -= 1;
        // Spread a little in time and keep the dots small: a stream, not a procession.
        this.add(stream.curve, true, "ask", now - Math.random() * dt, travelMs, 1.7 + Math.min(1.2, stream.mbps / 30));
      }
    }
    const alive: Dot[] = [];
    for (const dot of this.dots) {
      const t = (now - dot.startedAt) / dot.durationMs;
      if (t >= 1) {
        dot.halo.remove();
        dot.core.remove();
        continue;
      }
      if (t < 0) {
        alive.push(dot);
        continue;
      }
      const eased = t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
      const along = dot.reverse ? 1 - eased : eased;
      const at = pointOn(dot.curve, along);
      // Fade in and out at the ends so a dot appears from, and lands in, a circle.
      const fade = Math.min(1, t * 6, (1 - t) * 6);
      dot.core.setAttribute("cx", at.x.toFixed(1));
      dot.core.setAttribute("cy", at.y.toFixed(1));
      dot.halo.setAttribute("cx", at.x.toFixed(1));
      dot.halo.setAttribute("cy", at.y.toFixed(1));
      dot.core.style.opacity = String(fade);
      dot.halo.style.opacity = String(fade * 0.22);
      alive.push(dot);
    }
    this.dots = alive;
    this.report(this.dots.length > 0 || this.streams.size > 0);
    if (this.dots.length > 0 || this.streams.size > 0) this.frame = requestAnimationFrame(this.tick);
    else this.last = 0;
  };
}
