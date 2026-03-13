# HipHipParquet

A Windows desktop application for viewing, editing, and profiling structured data files with integrated data quality assessment.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![WPF](https://img.shields.io/badge/UI-WPF-blue)
![License](https://img.shields.io/badge/license-MIT-green)

## Overview

HipHipParquet provides data analysts and engineers with a local, offline tool for inspecting, editing, and assessing the quality of structured data files on Windows. It supports multiple file formats and delivers automated quality analysis without external tooling or cloud dependencies.

## Supported Formats

| Format | Read | Write |
|--------|------|-------|
| Parquet | Yes | Yes |
| CSV / TSV | Yes | Yes |
| JSON / NDJSON | Yes | Yes |
| Excel (.xlsx) | Yes | No |

## Features

### File Operations

- Open files via File > Open, drag-and-drop onto the window, or command-line argument
- Inline cell editing with save back to the original format or any supported output format
- Save As and Export As for cross-format conversion
- Import options for CSV/TSV (delimiter, encoding, quote character, header detection, row skipping) and JSON (array vs. NDJSON, explicit schema), each with a live 5-row preview before loading
- Flatten Nested JSON: expands STRUCT columns into a flat columnar layout on import
- Automatic file-change detection with a prompt to reload when the source file is modified externally
- Recent files list (last 10 entries) accessible from File > Recent Files
- Unsaved changes protection on close

### Data Grid

- Sortable columns; click a column header to cycle ascending/descending
- Persistent row-number column (#) with row count displayed in the status bar
- Incremental loading for large files: the first 50,000 rows are shown by default, with Load Next 50K and Load All controls in the banner above the grid
- Numeric columns are right-aligned for easier scanning
- Column headers truncate with an ellipsis when space is constrained; the full name is available on hover
- Multi-cell selection via click-drag or Shift/Ctrl-click

### Filtering and Search

- Per-column filter dropdowns: click the arrow on any column header to open a checkbox list of up to 500 distinct values, with a search box within the dropdown and Select All / Clear controls. When the value list is truncated, the number of total distinct values is shown.
- Global search box in the status bar searches across all columns simultaneously (Ctrl+F to focus)
- Active filters are summarized in a status bar badge showing the count of active filters, with a one-click Clear All button
- Edit > Clear All Filters resets all column filters and the global search
- Filter state is preserved when reloading or re-importing a file

### Navigation

- Ctrl+G or the dropdown on the # column header opens a Go to Row dialog for jumping directly to a specific row number
- Sort Ascending / Sort Descending actions are available in the dropdown for every column header, in addition to clicking the header itself
- Ctrl+F focuses the Global Search box

### Clipboard

- Ctrl+C copies selected cells as tab-separated values (Excel-compatible)
- Ctrl+Alt+C copies as CSV
- Ctrl+Shift+C copies as TSV
- All copy operations have "with headers" variants available from the Edit menu and right-click context menu
- Copy Row(s) and Copy Column(s) options available in the context menu

### Schema Pane

- Displays file name, format, row count, and a full column list with inferred types
- Copy Schema button exports the schema as plain text to the clipboard
- Toggle visibility from View > Toggle Schema Pane

### Quality Review Panel

- **Composite quality scoring** across four dimensions: Completeness, Uniqueness, Validity, and Distribution, each scored 0-25 for a 0-100 total with a letter grade
- **Per-column statistical profiling**: null rates, distinct counts, min/max/mean/median, quartiles, standard deviation, outlier detection, top value frequencies, and distribution sparklines
- **Narrative findings with severity grouping**: automated plain-English findings organized into collapsible severity buckets (Needs Review, Fair, Good), with live counts per severity level and filter chips for isolating categories
- **Column profile table**: compact scrollable table with color-coded score badges, inline dimension scores, null percentages, and distinct counts; hover tooltips show extended statistics
- **Column sorting**: sort profiles by name, overall score, or null percentage (ascending or descending)
- **Metric-based filter**: limit the profile table to columns meeting a threshold (e.g., Null % > 10 or Quality Score < 60)
- **Dimensional group-by analysis**: quality breakdown by categorical column values
- **File comparison**: schema diff with side-by-side type comparison and column-level drift scoring; detects added, removed, and type-changed columns alongside statistical drift
- **HTML report export**: self-contained, printable quality report covering all scores, findings, and column profiles

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| Ctrl+O | Open file |
| Ctrl+S | Save |
| Ctrl+G | Go to row |
| Ctrl+F | Focus Global Search |
| Ctrl+C | Copy selection (TSV) |
| Ctrl+Alt+C | Copy selection as CSV |
| Ctrl+Shift+C | Copy selection as TSV |
| F1 | Open Help |

## Installation

### Pre-built Release

Download from the [Releases](https://github.com/jhew/HipHipParquet/releases) page.

**Installer (recommended):** Download `HipHipParquet-1.4.0-Setup.exe` and run it. The wizard installs the application and creates Start Menu and optional desktop shortcuts.

**Portable:** Download `HipHipParquet-1.4.0-Portable.zip`, extract to any folder, and run `HipHipParquet.exe`.

## Usage

**Opening a file** — Use File > Open, drag a file onto the window, or pass a path as a command-line argument. CSV/TSV and JSON files open an import options dialog with a live preview. The most recent import settings are remembered per file.

**Navigating data** — Click column headers to sort ascending or descending. Use the dropdown arrow on any header for sort actions, column filters, and (on the # column) Go to Row. The global search box in the status bar filters across all columns. Ctrl+F focuses it; Ctrl+G opens Go to Row.

**Filtering** — Click the dropdown arrow on a column header and select or deselect values from the checkbox list. Multiple column filters combine with AND logic. The status bar shows a count of active filters and a Clear All button. Edit > Clear All Filters resets everything at once.

**Editing and saving** — Double-click a cell to edit its value inline. Ctrl+S saves in place. File > Save As allows choosing a different path or format. File > Export As converts to a different format without replacing the original.

**Quality analysis** — The Quality Review panel opens alongside the grid by default (toggle with View > Toggle Quality Review Panel). Click Analyze to profile the loaded file. Use the severity filter chips to focus on findings that need attention. Sort and filter the column profile table to identify problem areas. Use Compare to diff against a second file, Group By to break down quality by a categorical dimension, and Export to save an HTML report.

**Schema** — The Schema pane on the left shows column names, types, and row count for the open file. Click Copy Schema to copy the full schema as text.

## Architecture

- **Framework**: .NET 8.0 + WPF
- **Data engine**: DuckDB.NET 1.1.3 — handles all file I/O, format detection, and statistical aggregation via in-process SQL
- **MVVM**: CommunityToolkit.Mvvm 8.3.2 for the Quality Review Panel; code-behind for the main window
- **Target platforms**: Windows 10/11, x64 and ARM64

### Project Structure

```
HipHipParquet/
  Controls/          Custom WPF controls (QualityGauge, Sparkline)
  Converters/        Value converters for WPF bindings
  Models/            Data models: column profiles, quality scores, file formats
  Services/          Business logic: file I/O, quality scoring, narrative generation, report export
  Tests/             xUnit unit tests
  ViewModels/        MVVM view model for the Quality Review Panel
  Views/             XAML windows and dialogs
```

## Acknowledgments

- [DuckDB](https://duckdb.org/) — in-process analytical SQL engine
- [Apache Parquet](https://parquet.apache.org/) — columnar storage format
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) — MVVM toolkit for .NET
- [WPF](https://docs.microsoft.com/en-us/dotnet/desktop/wpf/) — Windows desktop UI framework

