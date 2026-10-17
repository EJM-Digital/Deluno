import { CLIENT_KINDS, isUsenet } from "./clients.ts";
import { defineResend, defineTakeover } from "./resendcases.ts";

/**
 * Group `resend`, timing "during", the torrent clients and the single-file takeover (scripts/golden-path/suite/vm/scenarios/G04-Resend.ps1, #1208, #1226, #1242, #1262).
 * One stack for this file; its share of the clients runs one at a time on it. The shared scenario is tests/e2e/resendcases.ts.
 */
defineResend("during", CLIENT_KINDS.filter((k) => !isUsenet(k)), defineTakeover);
