# Hip Hip Parquet 🎉

A modern Windows desktop application for viewing and analyzing Parquet files.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![WPF](https://img.shields.io/badge/UI-WPF-blue)
![License](https://img.shields.io/badge/license-MIT-green)

## Overview

Hip Hip Parquet users to quickly view and analyze Parquet files with a clean, intuitive interface. Built with WPF and DuckDB.

## Features ✨

- **📂 Open Parquet Files** - Load and view Parquet files with ease
- **🔢 Row Numbers** - Persistent row numbers that maintain original position even when sorted
- **🔍 Column Search** - Search and filter data in each column independently
- **🌐 Global Search** - Search across all columns simultaneously from the status bar
- **⬆️⬇️ Sortable Columns** - Click column headers to sort data ascending or descending
- **📊 Schema Viewer** - Collapsible left pane showing file metadata, column types, and row counts
- **📋 Copy to Clipboard** - Copy selected cells as CSV or TSV for pasting into Excel/Sheets
- **🕐 Recent Files** - Quick access to your 10 most recently opened files
- **👁️ Toggle Views** - Show/hide schema pane and filter row for customized workspace
- **🎨 Modern UI** - Clean, Windows 11-style interface with resizable columns
- **⚡ Fast Performance** - Virtualized scrolling handles thousands of rows efficiently (powered by DuckDB)
- **🛡️ Error Handling** - Graceful error messages and crash prevention

## Tech Stack

- **Framework**: .NET 8.0 + WPF (Windows Presentation Foundation)
- **Data Engine**: DuckDB.NET for efficient Parquet file operations
- **Architecture**: MVVM pattern
- **Target Platform**: Windows 10/11 x64

## Getting Started

### Prerequisites

- Windows 10/11
- .NET 8.0 SDK or Runtime

### Building

```powershell
# Clone the repository
git clone https://github.com/jhew/HipHipParquet.git
cd HipHipParquet

# Build the project
dotnet build

# Run the application
dotnet run
```

## Usage

1. Launch the application
2. Click **File → Open** (or select from **Recent Files**) to load a `.parquet` file
3. View your data in the sortable, virtualized grid with row numbers
4. Use the **search boxes** above each column to filter specific columns
5. Use the **global search** box (bottom right) to search across all columns
6. Click **column headers** to sort data (row numbers persist to show original position)
7. Select cells and use **Edit → Copy** to copy as CSV/TSV
8. Toggle **Schema Pane** and **Filter Row** from the View menu to customize your workspace
9. Resize columns and the row number column as needed

## Project Structure

```
HipHipParquet/
├── Services/
│   └── ParquetService.cs          # DuckDB integration for Parquet operations
├── Views/
│   ├── MainWindow.xaml            # Main application UI
│   └── MainWindow.xaml.cs         # UI code-behind
├── App.xaml(.cs)                  # Application entry point with DI
└── HipHipParquet.csproj           # Project configuration
```

## Current Features (V1)

- ✅ Open and view Parquet files (all rows, no artificial limits)
- ✅ Persistent row numbers for easy reference
- ✅ Sortable data grid with virtualized scrolling
- ✅ Per-column search and filtering
- ✅ Global search across all columns
- ✅ Copy selection as CSV/TSV to clipboard
- ✅ Recent files list (up to 10 files)
- ✅ Collapsible schema pane and filter row
- ✅ Schema viewer with type icons
- ✅ Resizable columns including row number column
- ✅ Error handling and crash prevention
- ✅ Modern Windows UI with custom application icon

## Roadmap 🚀

### Planned Features
- [ ] Cell editing with type validation
- [ ] Undo/Redo functionality
- [ ] Save/Save As operations
- [ ] Export to CSV/Excel
- [ ] Column show/hide and reordering
- [ ] Advanced filtering UI
- [ ] Dark mode support
- [ ] Recent files list
- [ ] MSIX packaging for Microsoft Store

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- Built with [DuckDB](https://duckdb.org/) - An in-process SQL OLAP database
- Uses [WPF](https://docs.microsoft.com/en-us/dotnet/desktop/wpf/) for the user interface
- Inspired by the need for a simple, fast Parquet file viewer on Windows

---

Made with ❤️ for data enthusiasts
