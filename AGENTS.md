# Repository Guidelines

## 網站風格設計規範

新增頁面、調整 UI 或審查介面變更前，先閱讀 [design.md](design.md)，遵循網站的配色、字體、版面間距、共用元件、互動狀態、響應式及無障礙規範。優先重用 `wwwroot/app.css` 的 CSS 變數、既有 class 與 Razor 共用元件。修改共用視覺風格或響應式規則時，須在同一變更中更新設計文件，並檢查桌面、窄螢幕與條件面板顯示／隱藏的呈現。此文件用於網站風格設計，不是後端系統架構文件；獨立的 `site/` 介紹網站依其既有樣式維護。

## Project Structure & Module Organization

WorkLens is a .NET 10 Blazor Server application. UI pages and shared Razor components live in `Components/`; layout and navigation are under `Components/Layout/`. Keep business behavior in `Services/`, domain entities and enums in `Domain/`, and SQLite setup and compatibility upgrades in `Data/`. Static CSS, JavaScript, and vendored browser assets belong in `wwwroot/`. Cross-platform install, run, and smoke-test scripts are in `scripts/`. The xUnit project is located at `tests/WorkLens.Tests/`.

## Build, Test, and Development Commands

- `dotnet restore WorkLens.csproj` — restore NuGet dependencies.
- `dotnet build WorkLens.csproj --no-restore` — compile the application.
- `dotnet run` — run locally at `http://127.0.0.1:5077`.
- `dotnet test tests/WorkLens.Tests/WorkLens.Tests.csproj --no-restore` — run the full test suite.
- `dotnet test tests/WorkLens.Tests/WorkLens.Tests.csproj --configuration Release` — match the GitHub Release test configuration.
- `git diff --check` — catch whitespace errors before committing.

Push a semantic-version tag such as `v1.1.0` only when intentionally publishing; `.github/workflows/release.yml` builds and releases all supported packages.

## Coding Style & Naming Conventions

Use four-space indentation for C#, nullable reference types, and file-scoped namespaces. Use PascalCase for public types and members, camelCase for locals and private fields, and descriptive async names ending in `Async`. Razor route files use PascalCase (`Schedules.razor`); CSS classes use lowercase kebab-case. Keep UI event handlers thin and place reusable behavior behind a focused module interface in `Services/`.

## Testing Guidelines

Tests use xUnit. Name test classes after the subject (`SchedulePlannerTests`) and test methods by scenario and outcome, for example `Next_run_moves_to_the_next_selected_day`. Add regression tests for bug fixes, database upgrades, scheduling rules, and rendered configuration. Use temporary directories or in-memory SQLite databases; never depend on a developer’s WorkLens data.

## Commit & Pull Request Guidelines

History follows Conventional Commit-style subjects such as `feat: add ...` and `fix: correct ...`. Keep commits focused and imperative. Pull requests should explain user-visible behavior, database compatibility, and verification commands; link relevant issues and include screenshots for layout changes. Update `CHANGELOG.md` for release-facing changes.

## Security & Local Data

Never commit SQLite databases, logs, reports, repository paths, author emails, AI responses, cookies, tokens, or local `appsettings` overrides. Runtime data belongs outside the repository in the platform-specific WorkLens data directory. Preserve unrelated working-tree changes, and keep database upgrades additive and repeatable through `DatabaseInitializer`.
