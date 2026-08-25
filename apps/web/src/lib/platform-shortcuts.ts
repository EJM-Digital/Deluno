const APPLE_PLATFORM_PATTERN = /Mac|iPhone|iPad|iPod/i;

export function isApplePlatform(platform: string): boolean {
  return APPLE_PLATFORM_PATTERN.test(platform);
}

export function commandPaletteShortcut(platform = readPlatform()) {
  const apple = isApplePlatform(platform);
  return {
    label: apple ? "⌘ K" : "Ctrl K",
    ariaKeyshortcuts: apple ? "Meta+K" : "Control+K",
  } as const;
}

function readPlatform(): string {
  if (typeof navigator === "undefined") return "";
  return `${navigator.platform} ${navigator.userAgent}`;
}
