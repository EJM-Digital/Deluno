/**
 * Motion primitives for Deluno's UI choreography.
 *
 * Everything uses the design tokens from `index.css`:
 *   --dur-xs / --dur-sm / --dur-md / --dur-lg
 *   --ease-emphasis / --ease-standard / --ease-spring
 *   --stagger
 *
 * Hand-off between routes uses framer-motion for exit-animation support.
 * Within a page, intra-list reveals use CSS `.stagger` for zero JS cost.
 */

import * as React from "react";
import {
  AnimatePresence,
  motion,
  useReducedMotion,
  type HTMLMotionProps,
  type Variants
} from "framer-motion";
import { useLocation, useOutlet } from "react-router-dom";

/* ──────────────────────────────────────────────────────
   PAGE TRANSITION — wraps <Outlet /> in the layout
────────────────────────────────────────────────────── */

const pageVariants: Variants = {
  initial: { opacity: 0, y: 8, filter: "blur(6px)" },
  enter: {
    opacity: 1,
    y: 0,
    filter: "blur(0px)",
    transition: { duration: 0.32, ease: [0.2, 0.9, 0.22, 1] }
  },
  exit: {
    opacity: 0,
    y: -6,
    filter: "blur(4px)",
    transition: { duration: 0.16, ease: [0.4, 0, 0.2, 1] }
  }
};

const pageVariantsReduced: Variants = {
  initial: { opacity: 0 },
  enter: { opacity: 1, transition: { duration: 0.12 } },
  exit: { opacity: 0, transition: { duration: 0.08 } }
};

/**
 * Wrap this around `<Outlet />` in the app layout. Animates presence on
 * every navigation. Respects `prefers-reduced-motion`.
 */
export function PageTransition() {
  const location = useLocation();
  const outlet = useOutlet();
  const shouldReduce = useReducedMotion();
  const variants = shouldReduce ? pageVariantsReduced : pageVariants;
  const transitionKey = getRouteTransitionKey(location.pathname);

  return (
    <AnimatePresence mode="wait" initial={false}>
      <motion.div
        key={transitionKey}
        variants={variants}
        initial="initial"
        animate="enter"
        exit="exit"
        className="min-h-[60vh]"
      >
        {outlet}
      </motion.div>
    </AnimatePresence>
  );
}

function getRouteTransitionKey(pathname: string) {
  if (pathname.startsWith("/settings")) return "/settings";
  if (pathname.startsWith("/system")) return "/system";
  return pathname;
}

/* ──────────────────────────────────────────────────────
   STAGGER — viewport-aware cascade for cards/lists
────────────────────────────────────────────────────── */

const containerVariants: Variants = {
  hidden: {},
  show: {
    transition: { staggerChildren: 0.034, delayChildren: 0.02 }
  }
};

const itemVariants: Variants = {
  hidden: { opacity: 0, y: 10 },
  show: {
    opacity: 1,
    y: 0,
    transition: { duration: 0.36, ease: [0.2, 0.9, 0.22, 1] }
  }
};

const itemVariantsReduced: Variants = {
  hidden: { opacity: 0 },
  show: { opacity: 1, transition: { duration: 0.14 } }
};

type StaggerProps = Omit<HTMLMotionProps<"div">, "variants" | "initial" | "animate"> & {
  /** Delay the whole cascade (useful for below-the-fold groups). */
  delay?: number;
};

/** Parent stagger container. Children should use <StaggerItem>. */
export function Stagger({ children, delay = 0, className, ...rest }: StaggerProps) {
  const shouldReduce = useReducedMotion();
  const variants: Variants = shouldReduce
    ? { hidden: {}, show: { transition: { staggerChildren: 0 } } }
    : {
        hidden: {},
        show: {
          transition: { staggerChildren: 0.034, delayChildren: 0.02 + delay }
        }
      };

  return (
    <motion.div
      className={className}
      variants={variants}
      initial="hidden"
      animate="show"
      {...rest}
    >
      {children}
    </motion.div>
  );
}

type StaggerItemProps = Omit<HTMLMotionProps<"div">, "variants">;

/**
 * Child of <Stagger>. Rises 10px and fades in on reveal.
 *
 * Renders as a flex column so the single child card always fills the
 * item's box — which matters when the item sits inside a CSS grid with
 * `align-items: stretch` or spans multiple rows. Without this, cards
 * would collapse to their content height and leave gaps beside taller
 * neighbours (e.g. a row-span-2 queue tile).
 */
export function StaggerItem({ children, className, ...rest }: StaggerItemProps) {
  const shouldReduce = useReducedMotion();
  return (
    <motion.div
      variants={shouldReduce ? itemVariantsReduced : itemVariants}
      className={`flex flex-col [&>*]:flex-1 [&>*]:min-h-0${className ? ` ${className}` : ""}`}
      {...rest}
    >
      {children}
    </motion.div>
  );
}

/* ──────────────────────────────────────────────────────
   REVEAL — one-shot on-mount entrance for a single element
────────────────────────────────────────────────────── */

type RevealProps = Omit<HTMLMotionProps<"div">, "initial" | "animate"> & {
  delay?: number;
  /** @default 8 */
  offset?: number;
};

/** Single element that rises + fades in on mount. */
export function Reveal({ children, delay = 0, offset = 8, className, ...rest }: RevealProps) {
  const shouldReduce = useReducedMotion();
  if (shouldReduce) {
    return (
      <motion.div
        className={className}
        initial={{ opacity: 0 }}
        animate={{ opacity: 1, transition: { delay, duration: 0.14 } }}
        {...rest}
      >
        {children}
      </motion.div>
    );
  }
  return (
    <motion.div
      className={className}
      initial={{ opacity: 0, y: offset }}
      animate={{
        opacity: 1,
        y: 0,
        transition: { delay, duration: 0.32, ease: [0.2, 0.9, 0.22, 1] }
      }}
      {...rest}
    >
      {children}
    </motion.div>
  );
}

/* ──────────────────────────────────────────────────────
   PRESS — hover + tap micro-physics for interactive cards
────────────────────────────────────────────────────── */

type PressProps = Omit<HTMLMotionProps<"button">, "whileHover" | "whileTap"> & {
  /** @default true */
  lift?: boolean;
};

/** Button-style motion for cards / tiles with subtle physical response. */
export const Press = React.forwardRef<HTMLButtonElement, PressProps>(
  ({ children, lift = true, className, ...rest }, ref) => {
    const shouldReduce = useReducedMotion();
    return (
      <motion.button
        ref={ref}
        className={className}
        whileHover={shouldReduce || !lift ? undefined : { y: -2 }}
        whileTap={shouldReduce ? undefined : { scale: 0.98 }}
        transition={{ type: "spring", stiffness: 400, damping: 30, mass: 0.8 }}
        {...rest}
      >
        {children}
      </motion.button>
    );
  }
);
Press.displayName = "Press";
