# Bundled tracker definitions: notice

The `*.yml` files in this folder are an unmodified snapshot of the tracker
definitions in the public repository **Prowlarr/Indexers**
(https://github.com/Prowlarr/Indexers), directory `definitions/v11`
(Cardigann schema version 11), taken from commit
`a504e8a39980f86d1d2bf132eb06ee430a401660` (28 September 2026). 568
definitions: 81 public, 60 semi-private and 427 private trackers.

Where they come from, as that repository's own README states: the definitions
are synced daily from the **Jackett** project (https://github.com/Jackett/Jackett,
licensed GPL-2.0) with some differences, and follow the Cardigann definition
format. The Prowlarr/Indexers repository itself carries no separate licence
file. Each definition remains the work of the Jackett and Prowlarr
contributors who wrote and maintain it; Deluno did not write or change them.

Deluno reads them with its own tracker engine (Deluno.Integrations, Search/
Trackers). The engine is Deluno's own code; only these definition files are
third-party. They are built in so that every site works without Prowlarr or
Jackett running.

Updating is off by default. "Check for newer site files" in the tracker picker,
or the scheduled update a person turns on in Settings, General, Tracker
definitions, fetches newer copies of these files from the same repository into
Deluno's data folder; a copy there replaces the built-in one.
