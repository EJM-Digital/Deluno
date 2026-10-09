/**
 * The bell's rows (#1098): the same row shape for everything that needs you, whether it is about your media or
 * about Deluno. Status dot, bold one-line title, muted one-line reason, then the row's (at most two) buttons in
 * two fixed columns, so every row's first button lines up with every other row's first button.
 *
 * What the buttons are comes from `lib/alert-actions.ts`, which System's Health reads too: one definition, so
 * the bell and Health cannot disagree about what a problem offers.
 *
 * One quiet line under the list reaches what is worth a look but is not a problem and is never counted:
 * leftover folders and health suggestions.
 */
import type { ReactElement } from "react";
import { Link } from "react-router-dom";
import { StatusLed } from "../../components/ui/status-led";
import type { AlertAction, AlertActions } from "../../lib/alert-actions";
import type { BellRow } from "../../lib/bell-rows";
import { ActionButton, plural, send, UNIFORM_BUTTON, useAction } from "./downloads-kit";
import type { DashboardAttention } from "./attention";

export type Action = ReturnType<typeof useAction>;

const linkButton = `inline-flex ${UNIFORM_BUTTON} items-center whitespace-nowrap rounded-[10px] border border-hairline text-(length:--button-font-size-sm) font-medium text-foreground hover:bg-secondary focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring`;

/**
 * One row, the same in the bell's Your media and Deluno sections: status dot, bold
 * one-line title (a link when it has somewhere to go), muted one-line reason (both whole
 * in the tooltip), then the two fixed action columns. One padding and a hairline divider
 * on every row; on a phone the buttons drop under the text.
 */
export function NeedsYouRow({ tone, title, text, actions, to }: { tone: "bad" | "warn"; title: string; text: string; actions: ReactElement; to?: string }) {
  const titleClass = "block truncate max-md:whitespace-normal max-md:break-words text-(length:--type-body-sm) font-semibold text-foreground";
  return (
    <li className="grid grid-cols-[auto_minmax(0,1fr)] items-center gap-x-2.5 gap-y-1.5 border-b border-hairline px-(--card-pad-x) py-2.5 last:border-b-0 sm:grid-cols-[auto_minmax(0,1fr)_auto]">
      <StatusLed tone={tone} pulse />
      <div className="min-w-0 leading-tight">
        {to ? <Link to={to} title={title} className={`${titleClass} hover:underline focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring`}>{title}</Link> : <span title={title} className={titleClass}>{title}</span>}
        <span title={text} className="block truncate max-md:whitespace-normal max-md:break-words text-(length:--type-caption) text-muted-foreground">{text}</span>
      </div>
      <div className="col-start-2 sm:col-start-auto">{actions}</div>
    </li>
  );
}

/**
 * The same action area on every row: two fixed columns of one width, so every
 * row's first button lines up with every other row's first button and the second
 * with the second, whatever their words. A row with one action puts it in the
 * first column and leaves the second empty, so every row ends at the same edge.
 */
export function RowActions({ primary, secondary }: { primary: ReactElement; secondary?: ReactElement }) {
  return (
    <span className="grid grid-cols-[7.5rem_7.5rem] gap-1.5" data-row-actions>
      <span className="flex">{primary}</span>
      <span className="flex">{secondary ?? null}</span>
    </span>
  );
}

/** Runs an alert button's request and says what happened in plain words; a link button is a link and never gets here. */
export function runAlert(action: AlertAction, state: Action): void {
  const request = action.request;
  if (!request) return;
  void state.run(action.id, async () => {
    const result = await send(request.path, request.failure, { method: request.method, body: request.body });
    return result?.message ?? request.done;
  });
}

/**
 * The (at most two) buttons of one alert, as the bell and Health both draw them. Every button names what it
 * does on hover, and its accessible name says which problem it is for ("Find another Sintel (2010)").
 */
export function AlertButtons({ actions, subject, state }: { actions: AlertActions; subject: string; state: Action }) {
  const draw = (action: AlertAction): ReactElement =>
    action.to ? (
      <Link key={action.id} to={action.to} aria-label={`${action.label}: ${subject}`} title={action.hint} className={linkButton}>{action.label}</Link>
    ) : (
      <ActionButton
        key={action.id}
        uniform
        label={action.label}
        name={`${action.label}: ${subject}`}
        hint={action.hint}
        busyKey={action.id}
        state={state}
        variant={action.quiet ? "ghost" : "outline"}
        onClick={() => runAlert(action, state)}
      />
    );
  return <RowActions primary={draw(actions[0])} secondary={actions[1] ? draw(actions[1]) : undefined} />;
}

/** One bell row, from the shared rows. */
export function bellRowElement(row: BellRow, state: Action): ReactElement {
  return <NeedsYouRow key={row.key} tone={row.tone} title={row.title} text={row.text} to={row.to} actions={<AlertButtons actions={row.actions} subject={row.subject ?? row.title} state={state} />} />;
}

/**
 * The one quiet line for what is worth a look but is not a problem and is never
 * counted: leftover folders and health suggestions. Nothing when there are none.
 */
export function AlsoWorthALook({ attention }: { attention: Pick<DashboardAttention, "alsoWorthALook"> }) {
  const { leftovers, suggestions } = attention.alsoWorthALook;
  const quiet = [
    leftovers > 0 ? (
      <Link key="leftovers" to="/queue" className="text-primary hover:underline">{plural(leftovers, "leftover folder")}</Link>
    ) : null,
    suggestions > 0 ? (
      <Link key="suggestions" to="/system" className="text-primary hover:underline">{plural(suggestions, "suggestion")}</Link>
    ) : null
  ].filter(Boolean);
  if (quiet.length === 0) return null;
  return (
    <span className="flex flex-wrap items-center gap-x-1.5">
      <span>Also worth a look:</span>
      {quiet.map((node, index) => (
        <span key={index} className="inline-flex items-center gap-1.5">{index > 0 ? <span aria-hidden>·</span> : null}{node}</span>
      ))}
    </span>
  );
}
