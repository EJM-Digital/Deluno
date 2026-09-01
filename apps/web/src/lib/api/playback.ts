import { fetchJson } from "./client";
import type {
  PlaybackDeviceGroup,
  PlaybackDeviceProfile,
  PlaybackGoalCompilation,
  PlaybackGoalItem
} from "./types";

const PLAYBACK_BASE = "/api/playback";

export function fetchPlaybackDeviceProfiles() {
  return fetchJson<PlaybackDeviceProfile[]>(`${PLAYBACK_BASE}/device-profiles`);
}

export function fetchPlaybackDeviceGroups() {
  return fetchJson<PlaybackDeviceGroup[]>(`${PLAYBACK_BASE}/device-groups`);
}

export function fetchPlaybackGoals() {
  return fetchJson<PlaybackGoalItem[]>(`${PLAYBACK_BASE}/goals`);
}

export function fetchPlaybackGoalCompilation(goalId: string) {
  return fetchJson<PlaybackGoalCompilation>(`${PLAYBACK_BASE}/goals/${encodeURIComponent(goalId)}/compile`);
}
