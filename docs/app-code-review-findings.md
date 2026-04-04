# App Code Review Findings

Review date: April 3, 2026

Scope: deep application review of the current Windows desktop app codebase, using a native desktop app review lens adapted to the actual implementation (`WPF` on `.NET 8` with `DuckDB.NET`).

## Executive Summary

The app is functional and the current automated test suite is green, but there are several release-significant gaps around update trust, save robustness, and runtime dependency loading. The most serious risks are:

1. The updater downloads and executes an installer without signature or hash verification.
2. Choosing "save before closing" can still close the app after a failed save and lose edits.
3. Saves are not atomic, so interrupted writes can corrupt the destination file.
4. Excel support dynamically installs a DuckDB extension at runtime.

## Validation Performed

- Ran `dotnet test Tests/HipHipParquet.Tests.csproj`
  - Result: `141` passed, `2` skipped, `0` failed
- Ran `dotnet list HipHipParquet.csproj package --include-transitive --vulnerable`
  - Result: no vulnerable packages reported by the current NuGet advisory feed
- Ran `dotnet list HipHipParquet.csproj package --include-transitive --outdated`
  - Result: several packages are behind current releases, especially `DuckDB.NET.*` and `Microsoft.Extensions.*`
- Reviewed build, installer, updater, workspace/session persistence, file I/O, grid/query logic, report export, and accessibility/testability surfaces

## Findings

### Critical

#### 1. Updater executes downloaded installers without strong artifact verification

Severity: Critical

The updater currently trusts a downloaded installer if it comes from `https` and a GitHub-owned host, then launches it directly. There is no Authenticode verification, pinned hash verification, or signed manifest validation before execution.

Evidence:

- `Services/UpdateService.cs:67`
- `Services/UpdateService.cs:90`
- `Services/UpdateService.cs:123`
- `Views/MainWindow.xaml.cs:3466`
- `Views/MainWindow.xaml.cs:3508`
- `.github/workflows/release.yml:42`
- `.github/workflows/release.yml:89`

Impact:

- A compromised release asset, mis-issued artifact, or supply-chain incident could result in arbitrary code execution on user machines.
- The release workflow produces hashes, but the application does not consume or verify them.
- The release workflow also has no visible code-signing step.

Recommended fix:

- Sign installer artifacts.
- Verify Authenticode signatures before launch.
- Prefer a signed manifest or pinned checksum flow rather than trusting hostnames alone.

### High

#### 2. Failed save during window close can still discard unsaved changes

Severity: High

When the user chooses to save on close, `OnWindowClosing` awaits `SaveFileAsync`, but then proceeds to `_closingConfirmed = true` and closes the window regardless of whether the save actually succeeded. `SaveFileAsync` catches errors internally and only shows a message box.

Evidence:

- `Views/MainWindow.xaml.cs:105`
- `Views/MainWindow.xaml.cs:136`
- `Views/MainWindow.xaml.cs:154`
- `Views/MainWindow.xaml.cs:162`
- `Views/MainWindow.xaml.cs:1076`
- `Views/MainWindow.xaml.cs:1109`

Impact:

- A failed save caused by file locks, permissions, disk-full conditions, or path errors can still lead to window closure and data loss.

Recommended fix:

- Return a success/failure result from `SaveFileAsync`.
- Only continue closing when save completion is confirmed.

#### 3. Save path is not atomic

Severity: High

The save logic exports directly to the final destination via DuckDB `COPY`. There is no write-to-temp-then-replace pattern.

Evidence:

- `Services/ParquetService.cs:778`
- `Services/ParquetService.cs:793`
- `Services/ParquetService.cs:855`

Impact:

- Interrupted or partial writes can leave the destination file corrupted.
- This is especially risky for in-place saves of the user’s only copy.

Recommended fix:

- Save to a temporary sibling file.
- Validate success.
- Replace the original atomically where possible.
- Preserve a rollback path on failure.

#### 4. Excel support installs DuckDB extension code at runtime

Severity: High

Excel import depends on `INSTALL spatial; LOAD spatial;` at runtime.

Evidence:

- `Services/ParquetService.cs:22`
- `Services/ParquetService.cs:28`

Impact:

- `.xlsx` support depends on downloading and loading extension code outside the installer/update trust chain.
- Behavior can vary by connectivity and extension policy.
- This expands the application’s runtime trust surface.

Recommended fix:

- Decide explicitly whether runtime extension installation is acceptable.
- If not, bundle a vetted extension strategy or disable this capability by default.
- At minimum, document and constrain extension security settings.

### Medium

#### 5. "Load All" does not actually load all rows for multi-file parquet sets

Severity: Medium

The `Load All` button sets `_currentRowLimit` to the total row count, but the multi-file load path resets the effective limit back to the standard batch size.

Evidence:

- `Views/MainWindow.xaml.cs:255`
- `Views/MainWindow.xaml.cs:279`
- `Views/MainWindow.xaml.cs:290`
- `Views/MainWindow.xaml.cs:3224`
- `Views/MainWindow.xaml.cs:3236`
- `Views/MainWindow.xaml.cs:3243`

Impact:

- Users selecting "Load All" for a parquet file set still get the default `50,000` row batch.

Recommended fix:

- Thread the requested row limit through the multi-file load path instead of resetting it.

#### 6. Identifier escaping is inconsistent for user-controlled column names

Severity: Medium

Some SQL and `DataView.RowFilter` expressions interpolate column names directly or with incomplete escaping. Because headers come from user files, valid column names containing `"` or `]` can break queries and filters.

Evidence:

- `Services/ParquetService.cs:803`
- `Services/ParquetService.cs:982`
- `Services/ParquetService.cs:1237`
- `Services/ParquetService.cs:1297`
- `Views/MainWindow.xaml.cs:2302`
- `Views/MainWindow.xaml.cs:2325`

Impact:

- Save, profiling, grouped statistics, and filter operations may fail on unusual but valid column names.
- This is primarily a correctness and robustness issue, with some injection-resistance implications.

Recommended fix:

- Centralize identifier escaping for DuckDB SQL.
- Centralize column escaping for `DataView.RowFilter`.
- Add tests using hostile-but-valid column names.

#### 7. File watcher can report the app’s own saves as external changes

Severity: Medium

The watcher remains active during app-initiated saves and is only suppressed after a change notification arrives.

Evidence:

- `Views/MainWindow.xaml.cs:1009`
- `Views/MainWindow.xaml.cs:1076`
- `Views/MainWindow.xaml.cs:1097`
- `Views/MainWindow.xaml.cs:3349`
- `Views/MainWindow.xaml.cs:3545`

Impact:

- Users may get spurious "modified externally" prompts immediately after saving.

Recommended fix:

- Disable watcher notifications around app-initiated save/export operations.
- Re-enable only after the write flow is complete.

### Low

#### 8. CI and tests do not cover the riskiest desktop-app behaviors

Severity: Low

The repo has a release workflow, but no visible CI quality gate for tests, static analysis, accessibility checks, or packaging verification before publish.

Evidence:

- `.github/workflows/release.yml:16`
- `.github/workflows/release.yml:42`
- `Tests/PerformanceTests.cs:37`
- `Tests/PerformanceTests.cs:92`
- `Tests/UpdateServiceTests.cs:8`

Impact:

- The app has good unit coverage in some areas, but the highest-risk flows are still weakly protected:
  - updater download-and-run behavior
  - close/save data loss path
  - workspace restore
  - watcher/save interaction
  - very large file behavior

Recommended fix:

- Add a normal CI workflow that runs on PRs.
- Add targeted tests for save/close, updater trust decisions, workspace restore, and large-data operations.

## Build, Packaging, and Deployment Notes

- Project shape is straightforward and maintainable for a single-app solution:
  - `Controls/`
  - `Converters/`
  - `Models/`
  - `Services/`
  - `Tests/`
  - `ViewModels/`
  - `Views/`
- `HipHipParquet.csproj` targets `net8.0-windows` and declares `x86`, `x64`, and `ARM64`.
- Publish profiles exist for `win-x64`, `win-arm64`, and a `win-x64-dev` fast local profile.
- Inno Setup is configured for per-user install by default with `PrivilegesRequired=lowest`.
- Minimum Windows version is enforced in the installer as `10.0.19041`.

Gaps noted:

- No visible reproducible-build hardening.
- No visible signing step in the release workflow.
- No visible PR-time CI gate before release publish.

## Code Quality and Maintainability Notes

- `Views/MainWindow.xaml.cs` is very large at roughly `3,500+` lines and acts as a coordination hub for startup, file loading, save/export, drag-drop, watcher behavior, recent files, workspace state, and update flow.
- `ViewModels/QualityReviewViewModel.cs` is also large at roughly `1,000+` lines.
- This concentration does not make the app unmaintainable today, but it raises long-term regression risk and makes desktop-behavior testing harder.

Recommended refactoring direction:

- Extract save/export coordination into an application service.
- Extract workspace/session restore into a dedicated service.
- Extract update orchestration from `MainWindow`.
- Keep `MainWindow` focused on UI composition and command routing.

## External Dependency and CVE Follow-up

### Current advisory status

As of April 3, 2026, NuGet’s current vulnerability feed reported no vulnerable packages in the resolved dependency graph.

Validated command:

- `dotnet list HipHipParquet.csproj package --include-transitive --vulnerable`

### Dependency staleness

Even though the current NuGet audit was clean, several packages are behind the latest versions:

- `DuckDB.NET.Data.Full` `1.1.3` vs latest `1.5.0`
- `DuckDB.NET.Bindings.Full` `1.1.3` vs latest `1.5.0`
- `CommunityToolkit.Mvvm` `8.3.2` vs latest `8.4.2`
- `Microsoft.Extensions.*` family on `8.0.x` vs latest `10.0.5`
- `System.Text.Encoding.CodePages` `8.0.0` vs latest `10.0.5`

Interpretation:

- No currently reported NuGet advisories is good.
- This does not eliminate native-engine risk in `DuckDB`, and it does not guarantee future advisory cleanliness.

### DuckDB-specific note

DuckDB `v1.4.2`, released on November 12, 2025, included a fix for `CVE-2025-64429`, with release notes specifically encouraging users of DuckDB encryption to update.

Important limitation:

- I did not find evidence that HipHipParquet uses DuckDB encryption features directly.
- So this is not a confirmed exploitable issue in the app.
- It is still a signal that the bundled native engine is behind current security maintenance.

### Runtime extension security note

DuckDB’s extension model runs with the same privileges as the parent process. Because this app dynamically installs and loads the `spatial` extension for Excel support, that runtime path should be treated as part of the app’s attack surface.

## Accessibility, UI Automation, and Fuzzing Follow-up

### Accessibility and UI automation status

I did not find evidence that the app is currently prepared for deep UI Automation testing or accessibility auditing:

- No visible `AutomationProperties.*` usage in XAML
- No visible custom `AutomationPeer` implementations
- No UI test harness such as `WinAppDriver`, `FlaUI`, or equivalent
- No accessibility scanner integration

Examples of concern:

- Custom canvas-based controls in `Controls/QualityGaugeControl.xaml` and `Controls/SparklineControl.xaml` are unlikely to expose strong UI Automation semantics by default.
- The `SelectableText` style in `Views/QualityReviewPanel.xaml:60` removes tab stops.
- Search inputs rely on placeholder text or tooltip text rather than explicit accessible labeling:
  - `Views/MainWindow.xaml:150`
  - `Views/MainWindow.xaml:374`

What is present:

- Menu mnemonics and keyboard shortcuts exist:
  - `Views/MainWindow.xaml:15`
  - `Views/MainWindow.xaml:36`

Assessment:

- Basic keyboard support exists.
- Deep accessibility coverage remains a residual risk.

### Fuzzing status

I did not find a fuzzing harness for:

- file import paths
- CSV/JSON malformed input handling
- drag-drop path handling
- workspace/session state deserialization

There is also no dedicated malformed-input corpus or fuzzing tool integration in the repo.

Assessment:

- Deep fuzzing was not performed in this pass.
- Fuzz coverage remains a residual risk.

## Suggested Next Steps

1. Treat updater signing and verification as the top remediation.
2. Fix the close-after-failed-save bug before the next release.
3. Make saves atomic.
4. Decide on a supported security posture for DuckDB runtime extensions.
5. Fix multi-file parquet `Load All`.
6. Add identifier-escaping tests for hostile column names.
7. Add a PR-time CI workflow for tests and packaging checks.
8. Add accessibility metadata and a small WPF UI Automation smoke-test harness.
9. Add malformed-input regression tests or a fuzzing harness around import paths.

## External References

- NuGet advisory feed: <https://api.nuget.org/v3/index.json>
- DuckDB extension security guidance: <https://duckdb.org/docs/lts/operations_manual/securing_duckdb/securing_extensions.html>
- DuckDB `v1.4.2` release notes: <https://github.com/duckdb/duckdb/releases/tag/v1.4.2>
- DuckDB current releases: <https://github.com/duckdb/duckdb/releases>
- DuckDB.NET NuGet package: <https://www.nuget.org/packages/DuckDB.NET.Data/>
- Microsoft.Extensions.Hosting NuGet package: <https://www.nuget.org/packages/Microsoft.Extensions.Hosting/>
