# 2026-09-11 cross-device-sync

- [x] Restate goal: use a generic locally-synced folder and immutable JSONL batches so each WorkLens installation can read the complete shared work history.
- [x] Locate existing local collection, SQLite, report, and configuration patterns.
- [x] Add additive database schema and durable JSONL sync core.
- [x] Integrate merged local/remote history with reports and UI read-only state.
- [x] Add sync settings/status UI and background execution.
- [x] Add protocol and integration tests; run build, tests, and diff check.

## Risk & rollback

- Risk: medium. Sync can duplicate or expose work history if identities or folder setup are wrong.
- Rollback: disable sync; local source tables remain authoritative and unchanged. Remote replica rows and JSONL files are retained.

## Working notes

- Sync files are provider-neutral UTF-8 JSONL in a user-selected local folder. OneDrive is only an interoperability target.
- Remote records are immutable from this installation and never re-exported.

## Results

- Added provider-neutral local-folder JSONL sync, remote replicas, background polling, status UI, and merged deterministic report/CSV reads.
- Added durable `SyncProcessedBatches` content-hash checkpoints: previously verified batch filenames are skipped before opening, hashing, or parsing their contents; source evidence remains synchronized alongside work entries.
- Verified with the full xUnit suite (241 passed) using installed SDK 10.0.302; the repository SDK pin remains 10.0.303.
