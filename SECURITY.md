# Security Policy

## Supported versions

Deluno is early and moves quickly. Security fixes ship in the latest release candidate
(the newest `1.0.0-rc.N` on the [releases page](https://github.com/jampat000/Deluno/releases)).
Older release candidates do not get backported fixes, so update to the latest one to receive them.

## Reporting a vulnerability

Please do not open a public GitHub issue for a vulnerability that is not fixed yet. Report it
privately through GitHub's private vulnerability reporting:

1. Open the [Security tab](https://github.com/jampat000/Deluno/security) of this repository.
2. Choose **Report a vulnerability**.

Include:

- the area affected and the Deluno version (System shows it)
- steps to reproduce
- what someone could do with it
- any proof-of-concept details needed to reproduce it

## What is in scope

- Signing in, sessions and API keys: getting past the login, or reading or changing data without one.
- Anything that lets one request read or write files outside the folders Deluno is set up to use.
- Secrets (passwords, API keys, tokens) being stored, logged or sent where they should not be.
- Code or command execution through Deluno's web or API surface.
- Deluno's installer and updater: running something other than what was published.

Not in scope: problems in the download clients, indexers, media servers or processors Deluno talks to
(report those to their own projects), a vulnerability that needs the attacker to already control the
machine Deluno runs on, and exposing Deluno directly to the internet without a reverse proxy or other
protection. Deluno is built for one person on a home network.

## What to expect

- Reports are acknowledged within 7 days.
- A confirmed vulnerability is fixed in a new release, and disclosed once that release is available.
