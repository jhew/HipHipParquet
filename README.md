# Hip Hip Parquet 🎉

A modern Windows desktop application for viewing and analyzing Parquet files.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![WPF](https://img.shields.io/badge/UI-WPF-blue)
![License](https://img.shields.io/badge/license-MIT-green)

## Overview

Hip Hip Parquet empowers data analysts and business users to quickly view and analyze Parquet files with a clean, intuitive interface. Built with WPF and DuckDB for reliable performance.

## Features ✨

- **📂 Open Parquet Files** - Load and view Parquet files with ease
- **🔍 Column Search** - Search and filter data in each column independently
- **⬆️⬇️ Sortable Columns** - Click column headers to sort data ascending or descending
- **📊 Schema Viewer** - View file metadata, column types, and row counts with type icons
- **🎨 Modern UI** - Clean, Windows 11-style interface
- **⚡ Fast Performance** - Powered by DuckDB for efficient data handling
- **🛡️ Error Handling** - Graceful error messages and crash prevention

## Tech Stack

- **Framework**: .NET 8.0 + WPF (Windows Presentation Foundation)
- **Data Engine**: DuckDB.NET for efficient Parquet file operations
- **Architecture**: MVVM pattern with dependency injection
- **Target Platform**: Windows 10/11 x64

## Getting Started

### Prerequisites

- Windows 10/11
- .NET 8.0 SDK or Runtime

### Building

```powershell
# Clone the repository
git clone https://github.com/yourusername/HipHipParquet.git
cd HipHipParquet

# Build the project
dotnet build

# Run the application
dotnet run
```

## Usage

1. Launch the application
2. Click **File → Open** to select a `.parquet` file
3. View your data in the sortable grid
4. Use the search boxes above each column to filter data
5. Click column headers to sort ascending or descending
6. View schema information in the left panel

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

- ✅ Open and view Parquet files
- ✅ Sortable data grid with all columns
- ✅ Per-column search and filtering
- ✅ Schema viewer with type information
- ✅ Error handling and crash prevention
- ✅ Modern Windows UI

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
