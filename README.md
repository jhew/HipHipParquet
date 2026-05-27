# HipHipParquet

HipHipParquet is a Windows desktop application for loading, exploring, editing, and profiling structured data files entirely on your local machine.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![WPF](https://img.shields.io/badge/UI-WPF-blue)
![License](https://img.shields.io/badge/license-MIT-green)

## Overview

HipHipParquet provides a self-contained workflow for data inspection and quality review with no external service dependency. It combines file import, interactive grid editing, SQL querying, schema inspection, automated quality analysis, and a built-in Markdown editor in a single desktop application.

---

## Supported Formats

| Format | Read | Write |
|--------|------|-------|
| Parquet (.parquet, .pqt) | Yes | Yes |
| Snappy-compressed Parquet | Yes | Yes |
| Split / sharded Parquet | Yes | Yes |
| CSV / TSV / TAB | Yes | Yes |
| JSON / JSONL / NDJSON | Yes | Yes |
| Excel (.xlsx, .xls) | Yes | No |

---

## Walkthrough

### Opening Files

Files can be opened from the File menu, dragged directly onto the application window, or passed as a command-line argument. When opening CSV, TSV, or JSON files, an import options dialog allows you to configure delimiters, encoding, and other format-specific settings before the data is loaded. Multi-part or sharded Parquet files are automatically combined into a single logical table.

For large datasets, HipHipParquet loads in batches of 50,000 rows. Additional rows can be loaded on demand or all at once, keeping the interface responsive regardless of file size.

The application maintains a recent files list and restores the previous workspace snapshot on startup.

### Exploring Data

Loaded data is displayed in a tabular grid with sortable columns, row numbering, and alternating row shading. The row count and any active filters are shown in the status bar.

**Global search** (Ctrl+F) filters the visible data in real time across all columns. **Per-column filters** allow narrowing to specific values, excluding values, or isolating blank cells. All active filters can be cleared from the status bar or the Edit menu. Filter state, column sort order, and column visibility are preserved across reloads.

The **Go to Row** command (Ctrl+G) jumps directly to a numbered row in large datasets.

### Editing

Cells can be edited inline by double-clicking or typing directly in the grid. The status bar shows a pending changes badge alongside a quick-save button whenever unsaved edits are present.

The Edit menu and grid context menu provide row-level operations including delete, keep only selected rows, duplicate, insert blank rows, and deduplicate. Cell-level operations include set to null, fill down, trim whitespace, and find/replace within a selection. The last edit action can be undone with Ctrl+Z.

Changes are saved back to the original file with Ctrl+S. The **Save As** and **Export As** commands allow writing to a new file or converting to a different supported format.

### Copying Data

Selection can be copied to the clipboard in multiple formats from the Edit menu and grid context menu: plain copy, copy with headers, CSV, CSV with headers, TSV, row-scoped JSON, and column-scoped variants. Ctrl+C, Ctrl+Alt+C, and Ctrl+Shift+C provide quick access to the most common copy formats.

### Schema Explorer

The schema pane on the left lists every column along with its inferred data type. Columns can be searched by name or type, and clicking an entry jumps the grid to that column. The full schema summary can be copied to the clipboard. The pane can be hidden from the View menu to reclaim horizontal space.

### Query Hub

The Query Hub panel sits above the data grid and provides a SQL editor powered by DuckDB. One or more files can be registered as named aliases and queried together using standard SQL. Query results are displayed in the main grid. A result set can be materialised as the active editable working set with the **Load as Working Set** command.

Frequently used queries can be saved and reloaded by name. The panel also maintains a sequential list of notebook blocks representing transformation and query steps, which can be individually removed or cleared in bulk.

Notebook actions provide one-click checks for null and empty values, duplicates, and regex patterns. Schema templates can be saved from the current source and subsequently used to validate incoming files. The Query Hub panel can be collapsed to a slim bar to maximise grid space.

### Quality Review

The Quality Review panel analyses the current dataset and produces a composite quality score across four dimensions:

- **Completeness** — proportion of non-null values per column.
- **Uniqueness** — ratio of distinct values to total rows.
- **Validity** — conformance to expected patterns and types.
- **Distribution** — balance and spread of values across the column.

Each dimension is visualised with a colour-coded progress bar. Per-column statistics, null rates, outlier indicators, and value distributions are available in the detailed findings table, which can be filtered and sorted by severity.

Analysis can be cancelled mid-run. A "Refresh recommended" badge appears when the loaded data has changed since the last analysis. Results can be exported as a self-contained HTML report. The Quality Review panel can be toggled from the View menu.

### Workspace Management

Named views save the current filter, search, sort, and column visibility state so that frequently used configurations can be reapplied instantly. A full workspace snapshot captures pane sizes, visibility settings, and the active file, and can be restored on a later session.

### Markdown Helper

The Markdown Helper is a lightweight editor and live preview tool for markdown files, available either as an embedded side panel within the main workspace or as a separate pop-out window.

In the embedded panel, the helper sits to the right of the data grid and can be shown or hidden from the File menu or via the View menu toggle. In the pop-out window, the editor expands to a full resizable window and can be docked back into the main workspace at any time.

Both modes provide:

- A plain-text **Edit** tab with monospaced editing and full support for tabs and line breaks.
- A **Preview** tab that renders the markdown to HTML. The active rendering profile (CommonMark, GitHub-style, or Extended / Best Effort) is displayed in the status bar while the Preview tab is active.
- **New**, **Open**, **Save**, and **Save As** file operations.

The pop-out window additionally displays the current file path and provides a **Copy path** button for quick access to the file location.

Draft state — including unsaved content, file path, and selected rendering profile — is preserved when switching between embedded and pop-out modes.

---

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| Ctrl+O | Open file |
| Ctrl+S | Save |
| Ctrl+Z | Undo |
| Ctrl+G | Go to row |
| Ctrl+F | Focus global search |
| Ctrl+C | Copy selection |
| Ctrl+Alt+C | Copy selection as CSV |
| Ctrl+Shift+C | Copy selection as TSV |
| F1 | Keyboard shortcuts help |

---

## Installation

Download the installer or portable package from the releases page:

https://github.com/jhew/HipHipParquet/releases

---

## Technical Stack

- .NET 8 and WPF
- DuckDB.NET for file I/O and SQL analytics
- CommunityToolkit.Mvvm for view model binding
- Markdig for markdown rendering
- xUnit test suite

## Project Layout

```text
HipHipParquet/
  Controls/       Custom WPF controls (sparkline, quality gauge)
  Converters/     Value converters for data binding
  Models/         Data models and domain types
  Services/       File I/O, quality analysis, workspace persistence
  Tests/          xUnit test suite
  ViewModels/     Observable view models
  Views/          XAML views and code-behind
```

## License

MIT
