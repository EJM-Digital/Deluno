/**
 * The dialog building blocks every Deluno drawer, dialog and sheet is made from: Radix's own parts, with one change.
 * `Content` hands focus back to whatever had it when the dialog opened, however it closes (Escape, the close button,
 * a save, a click outside). Radix only does that for a `Dialog.Trigger`; Deluno opens its dialogs from state, so
 * without this a keyboard user who pressed Escape landed on the page body and lost their place (#1340).
 */
import * as Dialog from "@radix-ui/react-dialog";
import * as React from "react";

export * from "@radix-ui/react-dialog";

const Content = React.forwardRef<React.ComponentRef<typeof Dialog.Content>, React.ComponentPropsWithoutRef<typeof Dialog.Content>>(
  ({ onOpenAutoFocus, onCloseAutoFocus, ...props }, ref) => {
    const opener = React.useRef<HTMLElement | null>(null);
    return (
      <Dialog.Content
        ref={ref}
        {...props}
        onOpenAutoFocus={(event) => {
          // Radix fires this before it moves focus into the dialog, so the active element is still the opener.
          const active = document.activeElement;
          opener.current = active instanceof HTMLElement && active !== document.body ? active : null;
          onOpenAutoFocus?.(event);
        }}
        onCloseAutoFocus={(event) => {
          onCloseAutoFocus?.(event);
          const target = opener.current;
          opener.current = null;
          if (event.defaultPrevented || !target || !target.isConnected) return;
          event.preventDefault();
          target.focus({ preventScroll: true });
        }}
      />
    );
  }
);
Content.displayName = "DialogContent";

export { Content };
