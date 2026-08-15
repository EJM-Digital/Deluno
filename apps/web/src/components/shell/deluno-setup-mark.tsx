import { cn } from "../../lib/utils";

/**
 * Deluno's configuration mark. It deliberately is not a borrowed product icon:
 * the nested paths describe media moving from intake through Deluno's rules into
 * a finished library.
 */
export function DelunoSetupMark({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 24 24" fill="none" aria-hidden="true" className={cn("h-5 w-5", className)}>
      <path d="M4.5 5.5h5v5h-5zM14.5 13.5h5v5h-5z" stroke="currentColor" strokeWidth="1.8" strokeLinejoin="round" />
      <path d="M9.5 8h3a2 2 0 0 1 2 2v3.5M9.5 16h2.5a2 2 0 0 0 2-2v-.5" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" />
      <path d="m11.7 14.2 1.8-1.8 1.8 1.8" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}
