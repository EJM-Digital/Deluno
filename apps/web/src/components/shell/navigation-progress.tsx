import { useNavigation } from "react-router-dom";

/**
 * A slim bar across the top of the window while the next screen is being
 * opened (#1081). It appears on the frame the click lands, so a click always
 * answers, even when the screen behind it is slow to come.
 */
export function NavigationProgress() {
  const navigation = useNavigation();
  if (navigation.state === "idle") return null;

  return (
    <div
      role="progressbar"
      aria-label="Opening the next screen"
      className="pointer-events-none fixed inset-x-0 top-0 z-120 h-0.5 overflow-hidden bg-transparent"
    >
      <div className="h-full w-1/3 animate-navigation-progress rounded-r-full bg-(--nav-accent,hsl(var(--primary)))" />
    </div>
  );
}
