# Hip Hip Parquet

A Windows desktop application for viewing, editing, and profiling data files with integrated quality assessment tools.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![WPF](https://img.shields.io/badge/UI-WPF-blue)
![License](https://img.shields.io/badge/license-MIT-green)

## Overview

Hip Hip Parquet is a desktop tool for data analysts and engineers who need to inspect, edit, and assess the quality of structured data files on Windows. It supports multiple file formats and provides automated quality analysis without requiring external tooling or cloud services.

## Supported Formats

| Format | Read | Write |
|--------|------|-------|
| Parquet | Yes | Yes |
| CSV / TSV | Yes | Yes |
| JSON / NDJSON | Yes | Yes |
| Excel (.xlsx) | Yes | No |

CSV and TSV files support configurable import options (delimiter, header detection, quote character, encoding, skip rows) with a live preview before loading.

## Features

### File Operations
- Open files via file picker, drag-and-drop, or command-line argument
- Edit cell values inline and save in Parquet, CSV, TSV, or JSON format
- Save As / Export As to convert between formats
- Automatic reload when a file is modified externally
- Recent files list (last 10 files)
- Unsaved changes protection on close

### Data Exploration
- Sortable columns with persistent row numbers
- Per-column search filters and global full-grid search
- Schema pane showing column names, types, and row count
- Row limiting with incremental load (50,000 rows per batch, or load all)
- Copy selection as CSV or TSV to clipboard

### Quality Review Panel
- Four-dimensional quality scoring: Completeness, Uniqueness, Validity, Distribution (0-100 total)
- Per-column statistical profiling: null rates, distinct counts, min/max/mean/median, quartiles, outlier detection, top value frequencies, distribution sparklines
- Automated narrative findings with severity indicators
- Dimensional group-by analysis (quality breakdown by categorical column values)
- File comparison with schema diff and column-level drift scoring
- Metric-based column filter (e.g. show columns where Null % > 10)
- Export self-contained HTML quality report

## Installation

### Pre-built Release
1. Download the latest release from the [Releases](https://github.com/jhew/HipHipParquet/releases) page
2. Extract the archive and run `HipHipParquet.exe`

### Build from Source

**Requirements:** Windows 10/11, [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```powershell
git clone https://github.com/jhew/HipHipParquet.git
cd HipHipParquet
dotnet build
dotnet run
```


## Usage

**Opening a file** — File > Open, drag a file onto the window, or pass a path as a command-line argument. CSV/TSV files prompt for import options with a live 5-row preview.

**Navigating data** — Click column headers to sort. Use per-column search boxes or the global search field to filter rows. For large files, use Load More or Load All in the banner at the bottom of the grid.

**Editing and saving** — Double-click a cell to edit. Ctrl+S saves in place; File > Save As lets you choose a different format or path.

**Quality analysis** — Open the Quality Review Panel from the View menu. Click Analyze to profile the loaded file. Use Compare to diff against a second file, Group By to break down quality by dimension, and Export to save an HTML report.

## Architecture

- **Framework**: .NET 8.0 + WPF
- **Data Engine**: DuckDB.NET — handles all file I/O, format detection, and statistical aggregation via in-process SQL
- **MVVM**: CommunityToolkit.Mvvm (Quality Review Panel); code-behind for the main window
- **Target**: Windows 10/11 x64 and ARM64

### Project Structure

```
HipHipParquet/
├── Converters/            # WPF value converters
├── Controls/              # Custom WPF controls (gauge, sparkline)
├── Models/                # Data models and file format types
├── Services/              # Business logic (file I/O, scoring, narrative, reports)
│   └── FileFormatDetector # Format detection and DuckDB reader expression builder
├── Tests/                 # xUnit unit tests
├── ViewModels/            # MVVM view model for the Quality Review Panel
└── Views/                 # XAML windows and user controls
```

## Contributing

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/your-feature`
3. Commit with a descriptive message and open a pull request

**Development tools:** Visual Studio 2022 or VS Code with the C# extension.

## License

MIT License. See [LICENSE](LICENSE) for details.

## Acknowledgments

- [DuckDB](https://duckdb.org/) — in-process analytical SQL engine
- [Apache Parquet](https://parquet.apache.org/) — columnar storage format
- [WPF](https://docs.microsoft.com/en-us/dotnet/desktop/wpf/) — Windows desktop UI framework
