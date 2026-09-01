import { fetchJson } from "./client";
import type { MediaPlanScenario, MediaPlanScenarioCompilation } from "./types";

const SCENARIO_BASE = "/api/media-plan-scenarios";

export function fetchMediaPlanScenarios(mediaType?: string): Promise<MediaPlanScenario[]> {
  const query = mediaType ? `?mediaType=${encodeURIComponent(mediaType)}` : "";
  return fetchJson<MediaPlanScenario[]>(`${SCENARIO_BASE}${query}`);
}

export function fetchMediaPlanScenarioCompilation(id: string, mediaType: string, name?: string): Promise<MediaPlanScenarioCompilation> {
  const params = new URLSearchParams({ mediaType });
  if (name?.trim()) params.set("name", name.trim());
  return fetchJson<MediaPlanScenarioCompilation>(`${SCENARIO_BASE}/${encodeURIComponent(id)}/compile?${params.toString()}`);
}
