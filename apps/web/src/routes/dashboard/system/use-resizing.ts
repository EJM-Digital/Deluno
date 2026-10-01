import { useEffect, useState } from "react";

/** True while the window is being resized and for a moment after, so nothing animates as the layout settles. */
export function useResizing(settleMs = 150): boolean {
  const [resizing, setResizing] = useState(false);
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    const onResize = () => {
      setResizing(true);
      if (timer) clearTimeout(timer);
      timer = setTimeout(() => setResizing(false), settleMs);
    };
    window.addEventListener("resize", onResize);
    return () => {
      window.removeEventListener("resize", onResize);
      if (timer) clearTimeout(timer);
    };
  }, [settleMs]);
  return resizing;
}
