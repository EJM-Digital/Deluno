/**
 * Field — the one way to lay out a form control in Deluno.
 *
 *   label (13px/500)
 *   control (36px, from Input / Select / Textarea / Switch / SegmentedControl)
 *   help or error (12.5px muted / destructive)
 *
 * No border, no per-field card. Related fields sit side by side inside a
 * `FieldRow`; groups of fields sit under a `DrawerSection` heading.
 *
 * `Field` owns the control's id: it generates one with `useId()` and exposes it
 * through `FieldContext`, so `Input`, `Select`, `Textarea` and `Switch` pick it
 * up automatically and the `<label htmlFor>` is always wired.
 */
import { createContext, useContext, useId, type ReactNode } from "react";
import { cn } from "../../lib/utils";

interface FieldContextValue {
  id: string;
  labelId: string;
  describedBy?: string;
  invalid: boolean;
}

const FieldContext = createContext<FieldContextValue | null>(null);

/** Read the id/description wiring supplied by the nearest `Field`. */
export function useFieldContext() {
  return useContext(FieldContext);
}

interface FieldProps {
  label: ReactNode;
  /** One short line under the control. Omitted when `error` is present. */
  help?: ReactNode;
  /** Field-level validation message; switches the control to the invalid state. */
  error?: ReactNode;
  /** Render the label but keep the control unlabelled visually (e.g. a switch row). */
  hideLabel?: boolean;
  optional?: boolean;
  className?: string;
  children: ReactNode;
}

export function Label({ htmlFor, className, children, ...props }: React.LabelHTMLAttributes<HTMLLabelElement>) {
  return (
    <label
      {...props}
      htmlFor={htmlFor}
      className={cn("density-label font-sans text-foreground", className)}
    >
      {children}
    </label>
  );
}

export function Field({ label, help, error, hideLabel = false, optional = false, className, children }: FieldProps) {
  const id = useId();
  const helpId = help || error ? `${id}-help` : undefined;
  const labelId = `${id}-label`;
  const value: FieldContextValue = { id, labelId, describedBy: helpId, invalid: Boolean(error) };

  return (
    <FieldContext.Provider value={value}>
      <div className={cn("grid min-w-0 content-start gap-1.5", className)}>
        <Label
          id={labelId}
          htmlFor={id}
          className={hideLabel ? "sr-only" : undefined}
        >
          {label}
          {optional ? <span className="ml-1 font-normal text-muted-foreground">· optional</span> : null}
        </Label>
        {children}
        {error ? (
          <p id={helpId} role="alert" className="density-help font-sans text-destructive">
            {error}
          </p>
        ) : help ? (
          <p id={helpId} className="density-help font-sans text-muted-foreground">
            {help}
          </p>
        ) : null}
      </div>
    </FieldContext.Provider>
  );
}

/** Two fields side by side on wide screens, stacked on narrow ones. */
export function FieldRow({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn("grid gap-[var(--grid-gap)] sm:grid-cols-2", className)}>{children}</div>;
}
