# Postprocessing

Shared rename / flatten / sample-filter pipeline that runs after
extraction completes and before the import event is raised.

- Sample / proof filter (`*sample*`, `*proof*`, `*screens*` patterns).
- Optional subdirectory flattening.
- Filename sanitization beyond OS-invalid chars.
- Output path resolution (per category / per library).

Lands in Phase 2.
