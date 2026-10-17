import { CLIENT_KINDS, isUsenet } from "./clients.ts";
import { defineResend } from "./resendcases.ts";

/**
 * Group `resend`, timing "after", the Usenet clients (scripts/golden-path/suite/vm/scenarios/G04-Resend.ps1, #1208, #1226, #1242).
 * One stack for this file; its share of the clients runs one at a time on it. The shared scenario is tests/e2e/resendcases.ts.
 */
defineResend("after", CLIENT_KINDS.filter(isUsenet));
