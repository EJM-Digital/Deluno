import { defineResend } from "./resendcases.ts";

/** Group `tvredoafter`: "I don't want it" then a resend of a TV episode, after Weir has finished the first send; a torrent and a Usenet client. Scenarios: tests/e2e/resendcases.ts. */
defineResend("after", ["qbittorrent", "sabnzbd"], undefined, "tv");
