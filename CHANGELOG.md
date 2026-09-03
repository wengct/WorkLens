# Changelog

All notable changes to WorkLens are documented in this file.

## [Unreleased]

## [1.1.0] - 2026-09-04

### Added

- Prompt template library with create, edit, duplicate, test, default, and archive actions.
- Configurable daily report, weekly report, daily backup, and weekly backup schedules.
- Schedule execution history with stage status, error details, next-run time, and manual execution.
- Per-schedule and manual AI Prompt selection with Prompt snapshots retained in AI job history.
- Runtime-configurable backup folder with write validation and a native folder picker.
- Release version display in the sidebar, sourced from the GitHub Release build version.
- Domain glossary for Prompt templates, schedule definitions, and schedule executions.

### Changed

- Scheduled reports now create the deterministic report first, then run AI organization and save the result automatically.
- AI failures preserve the deterministic report and record the failure instead of discarding usable output.
- Existing general, daily, and weekly Prompt settings are migrated into Prompt templates automatically.
- Report actions now use a compact aligned toolbar, clearer “重產制式摘要” wording, and an explicit saved-success message.
- GitHub and version information now share one compact sidebar footer row.
- Destructive and overwrite actions use reusable modal confirmation dialogs.
- Checkpoint reset, work-entry deletion, manual-source deletion, and report period changes prevent duplicate submissions.

### Database

- Adds `PromptTemplates`, `ScheduleDefinitions`, and `ScheduleExecutions` tables.
- Adds Prompt identity and content snapshot fields to `AiJobs`.
- Database changes are additive and applied automatically on first startup; existing work entries, evidence, reports, and backups are retained.

### Fixed

- Restored interrupted schedule executions are marked clearly instead of remaining in a running state.
- Windows backup folder selection is brought to the foreground with a topmost owner window.
- SQLite schedule history ordering no longer attempts an unsupported `DateTimeOffset` server-side sort.
- Sidebar release versions render as values instead of Razor expression text.

[Unreleased]: https://github.com/wengct/WorkLens/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/wengct/WorkLens/compare/v1.0.3...v1.1.0
