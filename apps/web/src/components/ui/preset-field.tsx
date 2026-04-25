import { useEffect, useState } from "react";
import { Input } from "./input";

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
}

export function PresetField({
  value,
  onChange,
  options,
  customLabel = "Custom",
  customPlaceholder,
  allowCustom = true,
  inputType = "text"
}: PresetFieldProps) {
  const [editingCustom, setEditingCustom] = useState(false);
  const optionValues = new Set(options.map((option) => option.value));
  const isCustom = allowCustom && (editingCustom || (value !== "" && !optionValues.has(value)));
  const selectValue = isCustom ? "__custom" : value;

  useEffect(() => {
    if (value !== "" && optionValues.has(value)) {
      setEditingCustom(false);
    }
  }, [optionValues, value]);

  return (
    <div className="space-y-2">
      <select
        value={selectValue}
        onChange={(event) => {
          if (event.target.value === "__custom") {
            setEditingCustom(true);
            onChange(value && !optionValues.has(value) ? value : "");
            return;
          }

          setEditingCustom(false);
          onChange(event.target.value);
        }}
        className="density-control-text h-[var(--control-height)] w-full rounded-[10px] border border-hairline bg-surface-2 px-[var(--field-pad-x)] text-foreground outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
        {allowCustom ? <option value="__custom">{customLabel}</option> : null}
      </select>

      {isCustom || selectValue === "__custom" ? (
        <Input
          type={inputType}
          value={value}
          onChange={(event) => {
            setEditingCustom(true);
            onChange(event.target.value);
          }}
          placeholder={customPlaceholder}
        />
      ) : null}
    </div>
  );
}
