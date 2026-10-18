import { defineStuckProgress } from "./stuckprogresscases.ts";

/** Group `tvstalledprogress`: a TV episode download stuck at 0 bytes, and just short of the end, is struck at its count; a torrent and a Usenet client. Scenarios: tests/e2e/stuckprogresscases.ts. */
defineStuckProgress("tv");
