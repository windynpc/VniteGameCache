# Changelog

## 0.2.10-beta.1

- Add opt-in ZIP/7z/RAR archive discovery, preview and per-game import, with on-demand extraction to the SSD cache.
- Add per-game archive source, cache and quarantine status to the Control Panel, plus an on-demand actual disk usage scan.
- Add safer single-game cleanup, quarantine restoration and clearer takeover command results.
- Improve MTool and translated executable detection, launch selection, and Wrapper process supervision.
- Keep archive updates from silently replacing nonempty caches and protect detected save data.

See `docs/releases/0.2.10-beta.1.md` for verification and remaining acceptance limits.

## 0.2.9-beta.1

- Limit combined VGC logs to 500 MB, reclaiming the oldest logs first.
- Rotate daily JSONL logs at 10 MB and include legacy plugin logs in retention.
- Keep log maintenance failures from interrupting game launches or cache operations.
- Preserve expired quarantine files and records when viewing cleanup suggestions.
- Run executable-detection and log-retention regression tests through plugin CI.

## 0.2.8-beta.1

First public beta packaging of the validated Vnite Game Cache codebase.

Highlights:

- Vnite takeover consistency checks and repair workflow.
- Schema 4 SQLite persistence and canonical Wrapper launch arguments.
- NAS/HDD to SSD cache preparation with zero-copy repeat launches.
- Chinese Control Panel for diagnostics, repair and batch operations.
- Safe cleanup, quarantine, LRU guidance and manual save management.
- Direct, translation-preferred and recognized MTool-template launch modes.

Known limitations:

- Automatic bidirectional save synchronization is not implemented.
- MTool integration remains a thin, template-based adapter.
- Executables are not code-signed.
