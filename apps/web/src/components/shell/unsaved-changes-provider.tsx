import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { ConfirmDialog } from "../ui/confirm-dialog";
import {
  UnsavedChangesContext,
  type UnsavedChangesRegistration
} from "../../hooks/use-unsaved-changes";

interface UnsavedChangesProviderProps {
  children: ReactNode;
}

/**
 * Gives every page-level editor the same navigation decision.
 *
 * Editors still own their save state and their pinned Save button. The shared
 * prompt submits the visible form, then the hook proceeds with the pending
 * navigation once that editor reports that it is clean. This keeps the prompt
 * useful without duplicating save handlers across every settings page.
 */
export function UnsavedChangesProvider({ children }: UnsavedChangesProviderProps) {
  const [registration, setRegistration] = useState<UnsavedChangesRegistration | null>(null);
  const registrationRef = useRef<UnsavedChangesRegistration | null>(null);
  const [saving, setSaving] = useState(false);

  const register = useCallback((next: UnsavedChangesRegistration) => {
    registrationRef.current = next;
    setRegistration((current) => {
      if (
        current?.id === next.id &&
        current.dirty === next.dirty &&
        current.blocker.state === next.blocker.state &&
        current.description === next.description
      ) {
        return current;
      }
      return next;
    });
  }, []);

  const unregister = useCallback((id: string) => {
    if (registrationRef.current?.id === id) registrationRef.current = null;
    setRegistration((current) => (current?.id === id ? null : current));
  }, []);

  const active = registration?.blocker.state === "blocked" && registration.dirty ? registration : null;

  useEffect(() => {
    if (!active) setSaving(false);
  }, [active]);

  function visibleForm() {
    const forms = Array.from(document.forms).filter((form) => {
      if (form.closest('[aria-hidden="true"]')) return false;
      return form.getClientRects().length > 0;
    });

    // A drawer is portalled after the page form, so prefer it when it is the
    // editor currently in front of the user.
    return forms.find((form) => form.closest('[role="dialog"]')) ?? forms[0] ?? null;
  }

  async function saveAndContinue() {
    const current = registrationRef.current;
    if (!current || current.blocker.state !== "blocked" || !current.dirty) return;

    setSaving(true);
    try {
      const save = current.saveRef.current;
      if (save) {
        await save();
      } else {
        const form = visibleForm();
        if (!form) {
          setSaving(false);
          return;
        }
        form.requestSubmit();
      }
    } catch {
      // The editor owns its error message. Keep the modal open so the user can
      // retry or choose Discard and continue after seeing that error.
      setSaving(false);
      return;
    }

    // The form owns the request and error state. Wait briefly for its dirty
    // flag to clear; a failed request leaves the prompt open with the form's
    // error visible underneath it.
    const deadline = Date.now() + 8000;
    while (Date.now() < deadline) {
      await new Promise<void>((resolve) => window.setTimeout(resolve, 50));
      const latest = registrationRef.current;
      if (!latest || !latest.dirty) break;
    }

    setSaving(false);
    const latest = registrationRef.current;
    if (latest?.blocker.state === "blocked" && !latest.dirty) latest.blocker.proceed();
  }

  function discardAndContinue() {
    const current = registrationRef.current;
    if (current?.blocker.state === "blocked") current.blocker.proceed();
  }

  const contextValue = useMemo(() => ({ register, unregister }), [register, unregister]);

  return (
    <UnsavedChangesContext.Provider value={contextValue}>
      {children}
      <ConfirmDialog
        open={active !== null}
        onOpenChange={(open) => {
          if (!open && active?.blocker.state === "blocked") active.blocker.reset();
        }}
        title="Unsaved changes"
        description={active?.description ?? "You have unsaved changes. Choose how to handle them before leaving."}
        presentation="decision"
        confirmLabel="Save and continue"
        confirmVariant="default"
        busy={saving}
        onConfirm={() => void saveAndContinue()}
        secondaryLabel="Discard and continue"
        secondaryVariant="destructive"
        onSecondary={discardAndContinue}
      />
    </UnsavedChangesContext.Provider>
  );
}
