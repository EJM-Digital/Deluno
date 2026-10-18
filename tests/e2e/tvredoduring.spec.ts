import { defineResend } from "./resendcases.ts";

/** Group `tvredoduring`: "I don't want it" then a resend of a TV episode, while Weir works on the first send; a torrent and a Usenet client. Scenarios: tests/e2e/resendcases.ts. */
defineResend("during", ["qbittorrent", "sabnzbd"], undefined, "tv");
