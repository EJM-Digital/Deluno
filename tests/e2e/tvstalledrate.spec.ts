import { defineStuckRate } from "./stuckratecases.ts";

/** Group `tvstalledrate`: a TV episode download with too few peers, and a slow one, is struck at its count; a torrent and a Usenet client. Scenarios: tests/e2e/stuckratecases.ts. */
defineStuckRate("tv");
