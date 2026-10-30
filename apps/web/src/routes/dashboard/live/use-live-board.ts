/**
 * The pushed Live events, applied (#1097). One hook for the Live tab: each event
 * patches the list it changes (see `realtime.ts`), and the two that have no list
 * of their own, a search under way and the jobs running, are kept here for the
 * "doing this second" cell and the Searching lane. Listening for any of these
 * also tells the server this screen is watching, so it reads the clients every
 * second while something downloads.
 */
import { useCallback, useState } from "react";
import { queryClient } from "../../../lib/route-cache";
import { sharedSources } from "../../../lib/shared-sources";
import { useLiveEvent } from "../../../lib/use-signalr";
import {
  applyClientState,
  applyDriveSpace,
  applyHandoffChanged,
  applyIndexerAnswer,
  applyJourneyStage,
  applyQueueRemoved,
  applyQueueStatus,
  applyRecoveryCase,
  applySearchesInFlight,
  applySpeedSample
} from "./realtime";

/** A library search under way, from `SearchCycleProgress`. */
export interface SearchProgress {
  libraryId: string;
  libraryName: string;
  state: "running" | "finished";
  checked: number;
  planned: number;
}

/** A background job running, from `JobChanged`. */
export interface JobNow {
  jobId: string;
  title: string;
}

export interface LiveBoardState {
  searches: ReadonlyMap<string, SearchProgress>;
  jobs: ReadonlyMap<string, JobNow>;
}

export function useLiveBoardEvents(): LiveBoardState {
  const [searches, setSearches] = useState<ReadonlyMap<string, SearchProgress>>(() => new Map());
  const [jobs, setJobs] = useState<ReadonlyMap<string, JobNow>>(() => new Map());

  useLiveEvent("JourneyStageChanged", (event) => applyJourneyStage(queryClient, event));
  useLiveEvent("ProcessorHandoffChanged", (event) => applyHandoffChanged(queryClient, event));
  useLiveEvent("RecoveryCaseChanged", (event) => applyRecoveryCase(queryClient, event));
  useLiveEvent("ClientStateChanged", (event) => applyClientState(queryClient, event));
  useLiveEvent("DriveSpaceChanged", (event) => applyDriveSpace(queryClient, event));
  useLiveEvent("DownloadSpeedSample", (event) => applySpeedSample(queryClient, event));
  useLiveEvent("SearchesInFlightChanged", (event) => applySearchesInFlight(queryClient, event));
  useLiveEvent("IndexerQueryRecorded", (event) => applyIndexerAnswer(queryClient, event));
  useLiveEvent("QueueItemStatusChanged", (event) => applyQueueStatus(queryClient, event));
  useLiveEvent("QueueItemRemoved", (event) => applyQueueRemoved(queryClient, event.id));
  // A new queue item has its client, size and route only in the reading itself: that one list is read again.
  useLiveEvent("QueueItemAdded", () => { void queryClient.invalidateQueries({ queryKey: sharedSources.telemetry.key, exact: true }); });

  useLiveEvent("SearchCycleProgress", useCallback((event) => {
    setSearches((current) => {
      const next = new Map(current);
      next.set(event.libraryId, { libraryId: event.libraryId, libraryName: event.libraryName ?? "your library", state: event.state, checked: event.checked, planned: event.planned });
      return next;
    });
  }, []));

  useLiveEvent("JobChanged", useCallback((event) => {
    setJobs((current) => {
      const next = new Map(current);
      if (event.state === "started") next.set(event.jobId, { jobId: event.jobId, title: event.title ?? "A background job" });
      else next.delete(event.jobId);
      return next.size === current.size && [...next.keys()].every((key) => current.has(key)) ? current : next;
    });
    // A job that has failed for good is a card in Needs you: the list of failed jobs is read again for it.
    if (event.state === "failed") void queryClient.invalidateQueries({ queryKey: sharedSources.failedJobs.key, exact: true });
  }, []));

  return { searches, jobs };
}
