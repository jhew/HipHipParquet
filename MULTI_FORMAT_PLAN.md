# Multi-Format Support Implementation Plan

> **Status:** All Phases Complete + UX/DX Improvements Complete  
> **Created:** 2026-03-01  
> **Goal:** Extend HipHipParquet to read CSV, TSV, JSON/JSONL, and Excel files alongside Parquet

---

## Architecture Insight

DuckDB (the app's query engine) natively supports `read_csv_auto()`, `read_json_auto()`, and `st_read()` (via the spatial extension for Excel) — meaning the **entire profiling/quality/narrative pipeline is already format-agnostic**. The only format-specific code is the `read_parquet()` function name in SQL strings within `ParquetService.cs`.

---

## Phase 1 — Abstraction Layer (Foundation)

- [x] **1.1** Create `Models/SupportedFileFormat.cs` — enum with `Parquet, Csv, Tsv, Json, Excel`
- [x] **1.2** Create `Services/FileFormatDetector.cs` — maps file extensions to `SupportedFileFormat` and returns the correct DuckDB reader function
- [x] **1.3** Generalize `ParquetService.cs` — replace every `read_parquet('{path}')` with dynamic `{readerFunction}('{path}')` based on detected format
- [x] **1.4** Update `SaveParquetFileAsync` — support exporting to CSV/JSON/Parquet via `COPY ... TO ... (FORMAT CSV|JSON|PARQUET)`

### Files affected
| File | Change |
|------|--------|
| `Models/SupportedFileFormat.cs` | **New** |
| `Services/FileFormatDetector.cs` | **New** |
| `Services/ParquetService.cs` | Modify — parameterize reader function |

---

## Phase 2 — UI Updates

- [x] **2.1** Update `OpenFileDialog` filter in `MainWindow.xaml.cs` to include `.csv`, `.tsv`, `.json`, `.jsonl`, `.xlsx`
- [x] **2.2** Update comparison dialog filter in `QualityReviewViewModel.cs` to accept all supported formats
- [x] **2.3** Update `SaveFileDialog` to offer CSV/JSON/Parquet export options
- [x] **2.4** Display detected file format in status bar and schema pane
- [x] **2.5** Update window title logic — remove "Parquet" hardcoding where it refers to file type

### Files affected
| File | Change |
|------|--------|
| `Views/MainWindow.xaml.cs` | Modify — dialog filters, status bar, title |
| `ViewModels/QualityReviewViewModel.cs` | Modify — comparison dialog filter |

---

## Phase 3 — CSV-Specific Options (MVP Enhancement)

- [x] **3.1** Add `Views/CsvOptionsDialog.xaml` — delimiter (comma/tab/pipe/semicolon), header row, encoding, quote char
- [x] **3.2** Pass CSV options to DuckDB via `read_csv('{path}', delim=',', header=true, ...)`
- [x] **3.3** Auto-detect delimiter using DuckDB's `read_csv_auto()` sniffing, then display detected settings

### Files affected
| File | Change |
|------|--------|
| `Views/CsvOptionsDialog.xaml` | **New** |
| `Views/CsvOptionsDialog.xaml.cs` | **New** |
| `Services/ParquetService.cs` | Modify — accept CSV options |

---

## Phase 4 — Format-Aware Quality Rules

- [x] **4.1** Add CSV-specific narrative findings (e.g., "mixed types detected — all values are strings in CSV")
- [x] **4.2** Add `SourceFormat` property to `FileProfile` so reports show the source format
- [x] **4.3** Update `ReportService` HTML output to display the source format
- [x] **4.4** Enable cross-format comparison (e.g., compare CSV against Parquet to validate ETL output)

### Files affected
| File | Change |
|------|--------|
| `Models/FileProfile.cs` | Modify — add `SourceFormat` property |
| `Services/NarrativeService.cs` | Modify — format-aware findings |
| `Services/ReportService.cs` | Modify — show format in HTML |
| `ViewModels/QualityReviewViewModel.cs` | Modify — cross-format comparison |

---

## Phase 5 — Additional Formats (Stretch)

- [x] **5.1** JSON/JSONL — wire up `read_json_auto()` (DuckDB native)
- [x] **5.2** Excel (.xlsx) — DuckDB `spatial` extension: `INSTALL spatial; LOAD spatial; SELECT * FROM st_read(...)`
- [x] **5.3** "Export As..." menu — Parquet→CSV, CSV→Parquet, etc.

### Files affected
| File | Change |
|------|--------|
| `Services/ParquetService.cs` | Modify — extension loading for Excel |
| `Views/MainWindow.xaml` | Modify — add Export As menu item |
| `Views/MainWindow.xaml.cs` | Modify — export handler |

---

## Recommended Implementation Order

**Phase 1 → Phase 2 → Phase 4 → Phase 3 → Phase 5**

This gets CSV and JSON working end-to-end (~100 lines of new code) before investing in the options dialog or stretch formats.

---

## Format Support Matrix (After Implementation)

| Format | Read | Write | Profile | Compare | Flatten | DuckDB Function |
|--------|:----:|:-----:|:-------:|:-------:|:-------:|-----------------|
| Parquet | ✅ | ✅ | ✅ | ✅ | — | `read_parquet()` |
| CSV | ✅ | ✅ | ✅ | ✅ | — | `read_csv_auto()` |
| TSV | ✅ | ✅ | ✅ | ✅ | — | `read_csv_auto(delim='\t')` |
| JSON/JSONL | ✅ | ✅ | ✅ | ✅ | ✅ | `read_json_auto()` |
| Excel | ✅ | ❌ | ✅ | ✅ | — | `st_read()` (spatial extension) |

---

## Phase 6 — UX & DX Improvements

- [x] **6.1** Branding: Window title updated to "HipHipParquet — Data Quality Viewer"
- [x] **6.2** Drag-and-drop: Drop a file on the window to open (AllowDrop + DragOver/Drop)
- [x] **6.3** CSV auto-detect: Removed auto-popup CSV dialog; DuckDB auto-detects silently. "Import Options..." menu item for on-demand CSV/TSV customization
- [x] **6.4** CSV dialog preview: 5-row live preview in the Import Options dialog that updates as settings change
- [x] **6.5** Row limit pagination: 50,000 row limit with "Load Next 50K" / "Load All" banner
- [x] **6.6** Status bar format badge: Colored badge showing current format (Parquet=green, CSV=blue, JSON=orange, etc.)
- [x] **6.7** Export As fix: Reuses stored ParquetService instance instead of creating a new connection
- [x] **6.8** Loading overlay: Semi-transparent overlay with progress bar for load/save/export operations
- [x] **6.9** Format detection info: Shows warning when unknown file extension (`.txt`, `.log`) defaults to CSV handling
- [x] **6.10** File watcher: FileSystemWatcher detects external changes and prompts to reload
- [x] **6.11** JSON flattening: "Flatten Nested JSON..." menu item detects STRUCT columns and expands them into flat columns
- [x] **6.12** Schema diff view: Side-by-side schema comparison table in Quality Review panel showing Added/Removed/Changed/Match columns
- [x] **6.13** xUnit tests: 47 unit tests for FileFormatDetector (format detection, DuckDB expressions, badges, filters)

### Files affected
| File | Change |
|------|--------|
| `Views/MainWindow.xaml` | Modify — branding, drag-drop, loading overlay, format badge, load-more banner, flatten/import menu items |
| `Views/MainWindow.xaml.cs` | Modify — all new handlers, row limiting, file watcher, service reuse |
| `Views/CsvOptionsDialog.xaml` | Modify — live preview grid, resizable dialog, option change events |
| `Views/CsvOptionsDialog.xaml.cs` | Modify — SetPreviewFile, RefreshPreview, DuckDB preview query |
| `Views/QualityReviewPanel.xaml` | Modify — schema diff expander with side-by-side table |
| `ViewModels/QualityReviewViewModel.cs` | Modify — SchemaDiffItems, BuildSchemaDiff() |
| `Services/ParquetService.cs` | Modify — row limit param, GetTotalRowCountAsync, GetFlattenedQueryAsync, LoadWithQueryAsync |
| `Services/FileFormatDetector.cs` | Modify — GetFormatBadgeColors(), IsUnknownExtension() |
| `HipHipParquet.csproj` | Modify — exclude Tests/ from main build |
| `Tests/HipHipParquet.Tests.csproj` | **New** — xUnit test project |
| `Tests/FileFormatDetectorTests.cs` | **New** — 47 unit tests |

---

## Key Locations in Codebase

| What | Where |
|------|-------|
| Format detection & DuckDB mapping | `Services/FileFormatDetector.cs` |
| Multi-format load/save/profile | `Services/ParquetService.cs` |
| File open/save dialogs | `Views/MainWindow.xaml.cs` |
| CSV import options dialog | `Views/CsvOptionsDialog.xaml` + `.cs` |
| Comparison & schema diff | `ViewModels/QualityReviewViewModel.cs` |
| DI registration | `App.xaml.cs` |
| Quality scoring | `Services/QualityScoreService.cs` |
| Narrative generation | `Services/NarrativeService.cs` |
| HTML report export | `Services/ReportService.cs` |
| Unit tests | `Tests/FileFormatDetectorTests.cs` |
