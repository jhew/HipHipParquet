# HipHipParquet

HipHipParquet is a Windows desktop application for loading, exploring, editing, and profiling structured data files locally.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![WPF](https://img.shields.io/badge/UI-WPF-blue)
![License](https://img.shields.io/badge/license-MIT-green)

## Overview

HipHipParquet provides a local workflow for data inspection and quality review with no external service dependency. It combines file import, grid editing, filtering, schema inspection, and automated quality analysis in one desktop app.

## Supported Formats

| Format | Read | Write |
|--------|------|-------|
| Parquet | Yes | Yes |
| CSV / TSV | Yes | Yes |
| JSON / NDJSON | Yes | Yes |
| Excel (.xlsx) | Yes | No |

## Core Capabilities

### File Handling

- Open files from menu, drag and drop, or command-line path.
- Open multiple parquet files as a single logical table.
- Use import options for CSV/TSV/JSON with preview before load.
- Save in place or export to a different supported output format.
- Detect external file changes and prompt to reload.
- Keep recent files list and restore workspace snapshot on startup.

### Grid and Editing

- Sort by clicking column headers.
- Use row numbering with row count display.
- Edit cells inline with unsaved-change tracking.
- Use pending changes badge with one-click Save.
- Load large datasets incrementally (50K batch controls).

### Filtering and Search

- Use per-column dropdown filters with searchable distinct values.
- Use global search across all columns.
- Clear filters from status bar or Edit menu.
- Preserve filter and sort state across reloads.

### Schema and Navigation

- View file metadata and column types in schema pane.
- Search schema by column name or type.
- Jump to a column from schema pane selection.
- Copy schema summary to clipboard.

### Quality Review

- Run profile analysis with a composite quality score.
- Review completeness, uniqueness, validity, and distribution dimensions.
- Inspect per-column statistics, null rates, outliers, and distributions.
- Filter and sort findings by severity and metric.
- Compare against another file for schema and drift insights.
- Export quality report to self-contained HTML.

### Workspace and Productivity

- Save and apply named views (filters/search/sort).
- Save and restore workspace snapshots including pane visibility/width.
- Use Jump List quick actions for recent files and shortcuts.
- Use keyboard shortcuts for open, save, search, go-to-row, and copy.

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| Ctrl+O | Open file |
| Ctrl+S | Save |
| Ctrl+G | Go to row |
| Ctrl+F | Focus global search |
| Ctrl+C | Copy selection (TSV) |
| Ctrl+Alt+C | Copy selection as CSV |
| Ctrl+Shift+C | Copy selection as TSV |
| F1 | Open help |

## Installation

Download installer or portable package from:

- https://github.com/jhew/HipHipParquet/releases

## Technical Stack

- .NET 8 and WPF
- DuckDB.NET for file I/O and analytics
- CommunityToolkit.Mvvm for Quality Review panel view model
- xUnit test suite

## Project Layout

```text
HipHipParquet/
  Controls/
  Converters/
  Models/
  Services/
  Tests/
  ViewModels/
  Views/
```

## License

MIT
