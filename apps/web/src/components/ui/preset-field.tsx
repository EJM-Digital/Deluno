/**
 * PresetField — "pick a preset or type your own" as one control.
 * Renders a Select (with an explicit placeholder when the value is empty and
 * no option matches) plus an Input when the custom option is active.
 * Picks up the surrounding Field's id/label like every other control.
 */
import { useEffect, useId, useMemo, useState } from "react";
import { Input } from "./input";
import { Select } from "./select";

export interface PresetOption {
  label: string;
  value: string;
}

interface PresetFieldProps {
  value: string;
  onChange: (value: string) => void;
  options: PresetOption[];
  customLabel?: string;
  customPlaceholder?: string;
  allowCustom?: boolean;
  inputType?: "text" | "number";
  /** Shown while nothing is chosen. Defaults to "Choose…". */
  placeholder?: string;
}

const CUSTOM = "__custom";

export function PresetField({ value, onChange, options, customLabel = "Custom", customPlaceholder, allowCustom = true, inputType = "text", placeholder = "Choose…" }: PresetFieldProps) {
  const [editingCustom, setEditingCustom] = useState(false);
  const customInputId = useId();
  const optionValues = useMemo(() => new Set(options.map((option) => option.value)), [options]);
  const isCustom = allowCustom && (editingCustom || (value !== "" && !optionValues.has(value)));
  const hasEmptyOption = optionValues.has("");
  const selectValue = isCustom ? CUSTOM : value;

  useEffect(() => {
    if (value !== "" && optionValues.has(value)) setEditingCustom(false);
  }, [optionValues, value]);

  return (
    <div className="grid gap-2">
      <Select
        value={selectValue}
        onChange={(event) => {
          if (event.target.value === CUSTOM) {
            setEditingCustom(true);
            onChange(value && !optionValues.has(value) ? value : "");
            return;
          }
          setEditingCustom(false);
          onChange(event.target.value);
        }}
      >
        {!hasEmptyOption && value === "" && !isCustom ? (
          <option value="" disabled>
            {placeholder}
          </option>
        ) : null}
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
        {allowCustom ? <option value={CUSTOM}>{customLabel}</option> : null}
      </Select>
      {isCustom ? (
        <Input
          id={customInputId}
          type={inputType}
          value={value}
          onChange={(event) => {
            setEditingCustom(true);
            onChange(event.target.value);
          }}
          placeholder={customPlaceholder}
          aria-label={customLabel}
        />
      ) : null}
    </div>
  );
}
