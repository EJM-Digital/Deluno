import { Fragment } from "react";
import { Plus, Trash2 } from "lucide-react";
import { OPERATOR_LABELS, type FilterOperator } from "../../lib/library-controls";
import { cn } from "../../lib/utils";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import { Select } from "../ui/select";

/** Mirrors `Deluno.Retention.RetentionStep`. */
export interface RetentionStep {
  type: "condition" | "block";
  op: "keep" | "include";
  condition?: string | null;
  name?: string | null;
  steps?: RetentionStep[] | null;
}

/** Mirrors `Deluno.Retention.RetentionField`. */
export interface RetentionField {
  id: string;
  label: string;
  hint: string;
  group: string;
  valueKind: string;
  operators: string[];
  options: string[] | null;
  source: "deluno" | "server" | "disk" | string;
  unsupportedOn: string[];
  /** Asked about one named person, so the rule says whose (#1006). */
  perPerson?: boolean;
}

/** Mirrors `Deluno.Retention.RetentionStepCount`. */
export interface RetentionStepCount {
  count: number;
  steps?: RetentionStepCount[] | null;
}

const GROUPS: { id: string; label: string }[] = [
  { id: "watching", label: "Watching" },
  { id: "server", label: "From the media servers" },
  { id: "part", label: "This season or episode" },
  { id: "requests", label: "Requests (Seerr)" },
  { id: "storage", label: "Storage" },
  { id: "title", label: "Title" },
  { id: "file", label: "File" },
  { id: "time", label: "Dates" },
  { id: "decision", label: "Deluno's decisions" }
];

/** The words the rule fields add to the library filter vocabulary. */
const RETENTION_OPERATOR_LABELS: Record<string, string> = {
  nobody: "nobody",
  anyone: "anyone",
  everyone: "everyone",
  beyond: "more than … days ago",
  within: "within the last … days",
  next: "within the next … days"
};

const LIST_OPERATOR_LABELS: Record<string, string> = {
  in: "includes any of",
  all: "includes all of",
  notin: "includes none of",
  set: "is not empty",
  unset: "is empty"
};

export function operatorLabel(field: RetentionField | undefined, operator: string) {
  if (field?.valueKind === "users" || field?.valueKind === "textList") {
    return RETENTION_OPERATOR_LABELS[operator] ?? LIST_OPERATOR_LABELS[operator] ?? operator;
  }

  if (field?.valueKind === "date" && field.source !== "deluno") {
    // A viewing either happened at some point or never did.
    if (field.group === "watching" && (operator === "set" || operator === "unset")) return operator === "unset" ? "never" : "at some point";
    return RETENTION_OPERATOR_LABELS[operator] ?? OPERATOR_LABELS[operator as FilterOperator] ?? operator;
  }

  return OPERATOR_LABELS[operator as FilterOperator] ?? RETENTION_OPERATOR_LABELS[operator] ?? operator;
}

export function takesValue(operator: string) {
  return !["set", "unset", "nobody", "anyone", "everyone"].includes(operator);
}

/**
 * A condition reads `field:operator:value`. A per-person field carries whose
 * answer it wants in its id, as `field@name` with the name percent-encoded so
 * a colon or an @ in it cannot break the condition (#1006).
 */
export function parseCondition(raw: string | null | undefined) {
  const [fullField = "", operator = "", ...rest] = (raw ?? "").split(":");
  const at = fullField.indexOf("@");
  const field = at >= 0 ? fullField.slice(0, at) : fullField;
  let person = "";
  if (at >= 0) {
    try {
      person = decodeURIComponent(fullField.slice(at + 1));
    } catch {
      person = fullField.slice(at + 1);
    }
  }

  return { field, person, operator, value: rest.join(":") };
}

export function encodeCondition(field: string, operator: string, value: string, person?: string | null) {
  const id = person === null || person === undefined ? field : `${field}@${encodeURIComponent(person)}`;
  return `${id}:${operator}:${takesValue(operator) ? value : ""}`;
}

/** A condition in a sentence, for lists and summaries. */
export function describeCondition(raw: string | null | undefined, fields: RetentionField[]) {
  const { field: id, person, operator, value } = parseCondition(raw);
  const field = fields.find((item) => item.id === id);
  const label = `${field?.label ?? id}${person ? ` (${person})` : ""}`;
  const shown = value.split("|").filter(Boolean).join(", ");
  if (!takesValue(operator)) return `${label} ${operatorLabel(field, operator)}`;
  if (field?.valueKind === "boolean") return `${label} is ${shown === "true" ? "yes" : "no"}`;
  const words = operatorLabel(field, operator);
  return words.includes("…") ? `${label} ${words.replace("…", shown)}` : `${label} ${words} ${shown}`;
}

function newCondition(op: RetentionStep["op"], fields: RetentionField[]): RetentionStep {
  const first = fields[0];
  return {
    type: "condition",
    op,
    condition: first ? encodeCondition(first.id, first.operators[0] ?? "", first.valueKind === "boolean" ? "true" : "", first.perPerson ? "" : null) : ""
  };
}

interface BuilderProps {
  steps: RetentionStep[];
  onChange: (steps: RetentionStep[]) => void;
  fields: RetentionField[];
  /** Kinds of the servers the rule reads, to mark fields none of them can answer. */
  serverKinds: string[];
  counts: RetentionStepCount[] | null;
  considered: number | null;
  noun: string;
}

/**
 * Maintainerr's step model, made readable (#494, DESIGN-008): "Keep only"
 * narrows what is left, "Also include" adds from the whole library, a block is
 * brackets. The number left after every step is shown beside it, so the order
 * a rule is written in is visible rather than hidden.
 */
export function RetentionStepBuilder({ steps, onChange, fields, serverKinds, counts, considered, noun }: BuilderProps) {
  return (
    <div className="grid gap-2">
      <div className="flex items-baseline justify-between gap-2 text-(length:--type-caption) text-muted-foreground">
        <span>Start with every {noun} Deluno holds</span>
        <span className="tabular-nums">{considered ?? "…"}</span>
      </div>
      <StepList steps={steps} onChange={onChange} fields={fields} serverKinds={serverKinds} counts={counts} label="Step" />
      <div className="flex flex-wrap gap-2">
        <Button type="button" variant="outline" size="sm" onClick={() => onChange([...steps, newCondition("keep", fields)])}>
          <Plus className="h-3.5 w-3.5" />
          Keep only…
        </Button>
        <Button type="button" variant="outline" size="sm" onClick={() => onChange([...steps, newCondition("include", fields)])} disabled={steps.length === 0}>
          <Plus className="h-3.5 w-3.5" />
          Also include…
        </Button>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => onChange([...steps, { type: "block", op: steps.length ? "include" : "keep", name: "", steps: [newCondition("keep", fields)] }])}
        >
          <Plus className="h-3.5 w-3.5" />
          Block
        </Button>
      </div>
    </div>
  );
}

interface ListProps {
  steps: RetentionStep[];
  onChange: (steps: RetentionStep[]) => void;
  fields: RetentionField[];
  serverKinds: string[];
  counts: RetentionStepCount[] | null | undefined;
  label: string;
  nested?: boolean;
}

function StepList({ steps, onChange, fields, serverKinds, counts, label, nested }: ListProps) {
  const replace = (index: number, next: RetentionStep | null) =>
    onChange(next === null ? steps.filter((_, position) => position !== index) : steps.map((step, position) => (position === index ? next : step)));

  return (
    <ol className="grid gap-2">
      {steps.map((step, index) => {
        const count = counts?.[index];
        // Keep only says what is left; Also include says what it added, so a
        // block's own last count and the count after it never read the same.
        const before = index === 0 ? null : counts?.[index - 1]?.count ?? null;
        const shown = !count ? "…" : step.op === "include" && before !== null ? `+${Math.max(0, count.count - before)}` : `${count.count} left`;
        const opLabel = index === 0 ? "Keep only" : undefined;
        return (
          <li key={index} className="grid gap-1">
            {step.type === "block" ? (
              <div className="grid gap-2 rounded-[10px] border border-hairline bg-surface-1/40 p-3">
                <div className="grid grid-cols-[9rem_minmax(0,1fr)_auto] items-center gap-2">
                  <OpSelect index={index} value={step.op} fixed={opLabel} label={`${label} ${index + 1}`} onChange={(op) => replace(index, { ...step, op })} />
                  <Input
                    aria-label={`${label} ${index + 1} block name`}
                    value={step.name ?? ""}
                    onChange={(event) => replace(index, { ...step, name: event.target.value })}
                    placeholder="Block name, e.g. Low rated"
                    autoComplete="off"
                  />
                  <Button type="button" variant="ghost" size="icon" aria-label={`Remove ${label.toLowerCase()} ${index + 1}`} onClick={() => replace(index, null)}>
                    <Trash2 className="h-3.5 w-3.5" />
                  </Button>
                </div>
                <StepList
                  steps={step.steps ?? []}
                  onChange={(inner) => replace(index, { ...step, steps: inner })}
                  fields={fields}
                  serverKinds={serverKinds}
                  counts={count?.steps}
                  label={`${label} ${index + 1} part`}
                  nested
                />
                <div className="flex flex-wrap gap-2">
                  <Button type="button" variant="ghost" size="sm" onClick={() => replace(index, { ...step, steps: [...(step.steps ?? []), newCondition("keep", fields)] })}>
                    <Plus className="h-3.5 w-3.5" />
                    Keep only…
                  </Button>
                  <Button type="button" variant="ghost" size="sm" onClick={() => replace(index, { ...step, steps: [...(step.steps ?? []), newCondition("include", fields)] })} disabled={(step.steps ?? []).length === 0}>
                    <Plus className="h-3.5 w-3.5" />
                    Also include…
                  </Button>
                </div>
              </div>
            ) : (
              <ConditionRow
                step={step}
                index={index}
                label={label}
                fixedOp={opLabel}
                fields={fields}
                serverKinds={serverKinds}
                onChange={(next) => replace(index, next)}
                onRemove={() => replace(index, null)}
              />
            )}
            <span className={cn("justify-self-end text-(length:--type-caption) tabular-nums text-muted-foreground", nested && "pr-1")}>
              {shown}
            </span>
          </li>
        );
      })}
    </ol>
  );
}

function OpSelect({ index, value, fixed, label, onChange }: { index: number; value: string; fixed?: string; label: string; onChange: (op: RetentionStep["op"]) => void }) {
  if (fixed) {
    // The first step always starts from the whole library, so there is nothing to choose.
    return <span className="text-(length:--type-body-sm) font-medium text-foreground">{fixed}</span>;
  }

  return (
    <Select
      aria-label={`${label} joins as`}
      value={value}
      onChange={(event) => onChange(event.target.value as RetentionStep["op"])}
      options={[
        { value: "keep", label: "Keep only" },
        { value: "include", label: "Also include" }
      ]}
      data-index={index}
    />
  );
}

interface RowProps {
  step: RetentionStep;
  index: number;
  label: string;
  fixedOp?: string;
  fields: RetentionField[];
  serverKinds: string[];
  onChange: (step: RetentionStep) => void;
  onRemove: () => void;
}

function ConditionRow({ step, index, label, fixedOp, fields, serverKinds, onChange, onRemove }: RowProps) {
  const { field: fieldId, person, operator, value } = parseCondition(step.condition);
  const field = fields.find((item) => item.id === fieldId);
  const name = `${label} ${index + 1}`;
  const unanswerable = (item: RetentionField) => item.source === "server" && serverKinds.length > 0 && serverKinds.every((kind) => item.unsupportedOn.includes(kind));
  const set = (nextField: string, nextOperator: string, nextValue: string, nextPerson?: string | null) => {
    const target = fields.find((item) => item.id === nextField);
    const who = target?.perPerson ? (nextPerson ?? person) : null;
    onChange({ ...step, condition: encodeCondition(nextField, nextOperator, nextValue, who) });
  };

  return (
    <div className="grid grid-cols-[9rem_minmax(0,1.3fr)_minmax(0,1fr)_minmax(0,1fr)_auto] items-center gap-2">
      <OpSelect index={index} value={step.op} fixed={fixedOp} label={name} onChange={(op) => onChange({ ...step, op })} />
      <Select
        aria-label={`${name} field`}
        value={fieldId}
        onChange={(event) => {
          const next = fields.find((item) => item.id === event.target.value);
          set(event.target.value, next?.operators[0] ?? "", next?.valueKind === "boolean" ? "true" : "");
        }}
      >
        {GROUPS.map((group) => {
          const inGroup = fields.filter((item) => item.group === group.id);
          return inGroup.length ? (
            <optgroup key={group.id} label={group.label}>
              {inGroup.map((item) => (
                <option key={item.id} value={item.id} disabled={unanswerable(item)}>
                  {unanswerable(item) ? `${item.label} (not on these servers)` : item.label}
                </option>
              ))}
            </optgroup>
          ) : (
            <Fragment key={group.id} />
          );
        })}
      </Select>
      <Select
        aria-label={`${name} test`}
        value={operator}
        onChange={(event) => set(fieldId, event.target.value, takesValue(event.target.value) ? value : "")}
        options={(field?.operators ?? []).map((item) => ({ value: item, label: operatorLabel(field, item) }))}
      />
      {field?.perPerson ? (
        <div className="grid grid-cols-2 gap-2">
          <Input
            aria-label={`${name} person`}
            value={person}
            onChange={(event) => set(fieldId, operator, value, event.target.value)}
            placeholder="Whose? Their name on the server"
            autoComplete="off"
          />
          <ValueInput field={field} operator={operator} value={value} name={name} onChange={(next) => set(fieldId, operator, next)} />
        </div>
      ) : (
        <ValueInput field={field} operator={operator} value={value} name={name} onChange={(next) => set(fieldId, operator, next)} />
      )}
      <Button type="button" variant="ghost" size="icon" aria-label={`Remove ${name.toLowerCase()}`} onClick={onRemove}>
        <Trash2 className="h-3.5 w-3.5" />
      </Button>
    </div>
  );
}

function ValueInput({ field, operator, value, name, onChange }: { field?: RetentionField; operator: string; value: string; name: string; onChange: (value: string) => void }) {
  if (!takesValue(operator)) {
    return <span aria-hidden />;
  }

  if (field?.valueKind === "boolean") {
    return <Select aria-label={`${name} value`} value={value} onChange={(event) => onChange(event.target.value)} options={[{ value: "true", label: "Yes" }, { value: "false", label: "No" }]} />;
  }

  if (field?.options?.length) {
    return <Select aria-label={`${name} value`} value={value} onChange={(event) => onChange(event.target.value)} placeholder="Choose…" options={field.options.map((option) => ({ value: option, label: option }))} />;
  }

  const days = operator === "beyond" || operator === "within" || operator === "next";
  const date = operator === "before" || operator === "after";
  const list = field?.valueKind === "users" || field?.valueKind === "textList" || operator === "in" || operator === "notin" || operator === "all";
  return (
    <Input
      aria-label={`${name} value`}
      type={days ? "number" : date ? "date" : ["integer", "decimal", "rating", "gigabytes", "minutes", "year"].includes(field?.valueKind ?? "") ? "number" : "text"}
      min={days ? 1 : undefined}
      value={list ? value.split("|").join(", ") : value}
      onChange={(event) => onChange(list ? event.target.value.split(",").map((item) => item.trim()).join("|") : event.target.value)}
      placeholder={days ? "days" : list ? "comma separated" : field?.valueKind === "gigabytes" ? "GB" : "value"}
      autoComplete="off"
    />
  );
}
