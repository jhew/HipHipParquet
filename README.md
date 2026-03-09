# Hip Hip Parquet

A Windows desktop application for viewing, editing, and profiling structured data files with integrated quality assessment.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![WPF](https://img.shields.io/badge/UI-WPF-blue)
![License](https://img.shields.io/badge/license-MIT-green)

## Overview

Hip Hip Parquet provides data analysts and engineers with a local, offline tool to inspect, edit, and assess the quality of structured data files on Windows. It supports multiple file formats and delivers automated quality analysis without external tooling or cloud dependencies.

## Supported Formats

| Format | Read | Write |
|--------|------|-------|
| Parquet | Yes | Yes |
| CSV / TSV | Yes | Yes |
| JSON / NDJSON | Yes | Yes |
| Excel (.xlsx) | Yes | No |

CSV and TSV imports support configurable options including delimiter, header detection, quote character, encoding, and row skipping, with a live preview before loading.

## Features

### File Operations
- Open files via file picker, drag-and-drop, or command-line argument
- Inline cell editing with save to Parquet, CSV, TSV, or JSON
- Save As / Export As for cross-format conversion
- Automatic reload on external file modification
- Recent files list (last 10 entries)
- Unsaved changes protection on close

### Data Exploration
- Sortable columns with persistent row numbers
- Per-column search filters and global full-grid search
- Schema pane displaying column names, types, and row count
- Incremental row loading (50,000 per batch) with option to load all
- **Multi‑cell selection** (click‑drag or Shift/Ctrl‑click) within the grid
- Clipboard copy of selected cells – use Edit ▶ Copy or right‑click for the context menu; include column names by choosing "with headers".
  The right‑click menu now includes separate entries for *Copy* (tab‑delimited) and *Copy as CSV* (comma‑delimited), both with header variants.  Keyboard shortcuts:
  Ctrl+C (TSV), Ctrl+Shift+C (TSV with headers), Ctrl+Alt+C (CSV).

### Quality Review Panel
- **Composite quality scoring** across four dimensions (Completeness, Uniqueness, Validity, Distribution), each scored 0--25 for a 0--100 total
- **Per-column statistical profiling**: null rates, distinct counts, min/max/mean/median, quartiles, outlier detection, top value frequencies, and distribution sparklines
- **Findings with severity grouping**: automated narrative findings organized into collapsible severity buckets (Needs Review, Fair, Good). Filter chips allow isolating findings by severity level, with live counts per category. Scales to 90+ findings across 30+ column datasets without losing context.
- **Condensed table-format column profiles**: column statistics displayed in a compact, scrollable table layout matching the HTML export, with color-coded score badges, inline dimension scores (C/U/V/D), null percentages, and distinct counts. Hover tooltips provide extended statistics including mean, median, and standard deviation.
- **Column sorting**: sort column profiles by name, score (ascending/descending), or null percentage (ascending/descending)
- **Metric-based column filter**: query columns by threshold (e.g., show columns where Null % > 10 or Quality Score < 60)
- **Dimensional group-by analysis**: quality breakdown by categorical column values
- **File comparison**: schema diff with side-by-side type comparison and column-level drift scoring
- **HTML report export**: self-contained, printable quality report with all scoring, findings, and column profiles

## Installation

### Pre-built Release

Download from the [Releases](https://github.com/jhew/HipHipParquet/releases) page.

**Installer (recommended):** Download `HipHipParquet-x.x.x-Setup.exe` and run it. The wizard installs the application and creates Start Menu and optional desktop shortcuts. Requires the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime?runtime=desktop&os=windows&arch=x64). The installer will prompt to download it if not already present.

**Portable:** Download `HipHipParquet-x.x.x-Portable.zip`, extract to any folder, and run `HipHipParquet.exe`. Requires the .NET 8 Desktop Runtime.

### Build from Source

Requirements: Windows 10/11, [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```powershell
git clone https://github.com/jhew/HipHipParquet.git
cd HipHipParquet
dotnet build
dotnet run
```

## Usage

**Opening a file** -- Use File > Open, drag a file onto the window, or pass a path as a command-line argument. CSV/TSV files prompt for import options with a live 5-row preview.

**Navigating data** -- Click column headers to sort. Use per-column search boxes or the global search field to filter rows. For large files, use Load More or Load All in the banner at the bottom of the grid.

**Editing and saving** -- Double-click a cell to edit. Ctrl+S saves in place. File > Save As allows choosing a different format or path.

**Quality analysis** -- Open the Quality Review Panel from the View menu. Click Analyze to profile the loaded file. Use the severity filter chips to focus on findings that need attention. Sort and filter column profiles to identify problem areas. Use Compare to diff against a second file, Group By to break down quality by dimension, and Export to save an HTML report.

## Architecture

- **Framework**: .NET 8.0 + WPF
- **Data Engine**: DuckDB.NET -- handles all file I/O, format detection, and statistical aggregation via in-process SQL
- **MVVM**: CommunityToolkit.Mvvm (Quality Review Panel); code-behind for the main window
- **Target**: Windows 10/11, x64 and ARM64

### Project Structure

```
HipHipParquet/
  Converters/            Value converters for WPF bindings
  Controls/              Custom controls (quality gauge, sparkline)
  Models/                Data models, quality scores, and file format types
  Services/              Business logic (file I/O, scoring, narrative generation, report export)
  Tests/                 xUnit unit tests
  ViewModels/            MVVM view model for the Quality Review Panel
  Views/                 XAML windows and user controls
```

## Contributing

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/your-feature`
3. Commit with a descriptive message and open a pull request

Development tools: Visual Studio 2022 or VS Code with the C# extension.

## License

MIT License. See [LICENSE](LICENSE) for details.

## Acknowledgments

- [DuckDB](https://duckdb.org/) -- in-process analytical SQL engine
- [Apache Parquet](https://parquet.apache.org/) -- columnar storage format
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) -- MVVM toolkit for .NET
- [WPF](https://docs.microsoft.com/en-us/dotnet/desktop/wpf/) -- Windows desktop UI framework
