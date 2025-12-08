# Hip Hip Parquet 🎉

A fast, modern Windows desktop application for viewing and editing Apache Parquet files.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![WPF](https://img.shields.io/badge/UI-WPF-blue)
![License](https://img.shields.io/badge/license-MIT-green)

## What is Hip Hip Parquet?

Hip Hip Parquet is a user-friendly desktop app that lets you open, view, search, edit, and save Parquet data files on Windows. Whether you're a data analyst, developer, or anyone working with Parquet files, this tool makes it easy to explore and modify your data without writing code.

**What are Parquet files?** Apache Parquet is a popular columnar storage format used in data engineering and analytics. It's commonly used with big data tools like Apache Spark, Pandas, and cloud data warehouses.

## Features ✨

### File Operations
- **📂 Open Parquet Files** - Load any `.parquet` file with a simple file picker or by right-clicking files in Windows Explorer
- **💾 Edit & Save** - Double-click any cell to edit values, then save changes back to Parquet format
- **📋 Save As** - Save your modified data to a new file
- **🕐 Recent Files** - Quick access to your 10 most recently opened files
- **⚠️ Unsaved Changes Warning** - Get prompted before closing if you have unsaved edits

### Data Exploration
- **🔢 Row Numbers** - Persistent row numbers that show original position even when sorted or filtered
- **⬆️⬇️ Sortable Columns** - Click any column header to sort data ascending or descending
- **🔍 Column Filters** - Search and filter individual columns independently
- **🌐 Global Search** - Search across all columns simultaneously to find any value
- **📊 Schema Viewer** - View file metadata, column names, data types, and row counts
- **📋 Copy to Clipboard** - Select cells and copy as CSV or TSV for use in Excel or Google Sheets

### User Experience
- **🎨 Clean Interface** - Modern, intuitive Windows design
- **↔️ Resizable Columns** - Drag column borders to adjust width (works in any window size)
- **👁️ Customizable Layout** - Show/hide the schema pane and search filters as needed
- **⚡ Fast Performance** - Handles large files efficiently using virtualized scrolling and DuckDB
- **🛡️ Error Handling** - Helpful error messages instead of crashes

## Getting Started

### Installation

#### Option 1: Download Pre-built Release (Easiest)
1. Go to the [Releases](https://github.com/jhew/HipHipParquet/releases) page
2. Download the latest `.zip` file
3. Extract and run `HipHipParquet.exe`

#### Option 2: Build from Source
If you want to build it yourself:

**Prerequisites:**
- Windows 10 or Windows 11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (free download from Microsoft)

**Steps:**
```powershell
# Clone the repository
git clone https://github.com/jhew/HipHipParquet.git
cd HipHipParquet

# Build the application
dotnet build

# Run it
dotnet run
```

### Using the Application

1. **Open a File**
   - Click **File → Open** and select a `.parquet` file
   - Or use **File → Recent Files** for quick access
   - Or right-click any `.parquet` file in Windows Explorer and select "Open with Hip Hip Parquet"

2. **Browse Your Data**
   - Scroll through rows and columns
   - Click column headers to sort
   - Use search boxes above columns to filter specific columns
   - Use the global search (bottom right) to search everywhere

3. **Edit Data**
   - Double-click any cell to edit
   - Press Enter to confirm or Esc to cancel
   - Window title shows `*` when you have unsaved changes

4. **Save Your Work**
   - Press **Ctrl+S** or click **File → Save** to save changes
   - Use **File → Save As** to create a new file
   - You'll be prompted to save if you try to close with unsaved changes

5. **Copy Data**
   - Select one or more cells
   - Click **Edit → Copy** (or Copy as CSV/TSV)
   - Paste into Excel, Google Sheets, or any text editor

6. **Customize Your View**
   - Use **View → Toggle Schema Pane** to show/hide file information
   - Use **View → Toggle Filter Row** to show/hide search boxes
   - Resize columns by dragging their borders
   - Resize the schema pane by dragging the divider

## For Developers

### Tech Stack
- **Framework**: .NET 8.0 + WPF (Windows Presentation Foundation)
- **Data Engine**: DuckDB.NET for Parquet read/write operations
- **Architecture**: Code-behind pattern with service layer
- **Target Platform**: Windows 10/11 x64

### Project Structure

```
HipHipParquet/
├── Assets/
│   └── app.ico                    # Application icon
├── Services/
│   └── ParquetService.cs          # DuckDB integration for Parquet I/O
├── Views/
│   ├── MainWindow.xaml            # Main UI layout
│   └── MainWindow.xaml.cs         # UI logic and event handlers
├── App.xaml(.cs)                  # Application entry point with DI
└── HipHipParquet.csproj           # Project configuration
```

### Key Features Implementation
- **Parquet Operations**: Uses DuckDB's `read_parquet()` and `COPY TO` commands for efficient file I/O
- **Virtualized Grid**: WPF DataGrid with row/column virtualization for performance
- **Filtering**: DataView.RowFilter with dynamic SQL-like LIKE expressions
- **Recent Files**: Stored in Windows Registry under `HKEY_CURRENT_USER\Software\HipHipParquet`

## Current Features

- ✅ Open and view Parquet files (unlimited rows)
- ✅ Edit cell values with live DataTable updates
- ✅ Save and Save As functionality
- ✅ Unsaved changes tracking and warnings
- ✅ Persistent row numbers (maintained through sorting/filtering)
- ✅ Sortable columns with virtualized scrolling
- ✅ Per-column search filters (additive with global search)
- ✅ Global search across all columns
- ✅ Copy selection as CSV/TSV
- ✅ Recent files list (10 most recent)
- ✅ Collapsible schema pane with type information
- ✅ Resizable columns (works in all window sizes)
- ✅ Right-click file association support
- ✅ Custom application icon
- ✅ Error handling with user-friendly messages

## Roadmap 🚀

### Potential Future Features
- [ ] Add new rows and columns
- [ ] Delete rows and columns
- [ ] Undo/Redo functionality
- [ ] Export to CSV/Excel formats
- [ ] Column reordering via drag-and-drop
- [ ] Advanced filter builder UI
- [ ] Data type validation on edit
- [ ] Dark mode / theme support
- [ ] Keyboard shortcuts guide
- [ ] MSIX packaging for Microsoft Store distribution
- [ ] Mac/Linux support via Avalonia UI port

## Contributing

Contributions are welcome! Whether you're fixing bugs, adding features, or improving documentation, your help is appreciated.

### How to Contribute
1. Fork the repository
2. Create a feature branch (`git checkout -b feature/YourFeature`)
3. Make your changes and test thoroughly
4. Commit with clear messages (`git commit -m 'Add feature: YourFeature'`)
5. Push to your fork (`git push origin feature/YourFeature`)
6. Open a Pull Request with a description of your changes

### Development Setup
- Install [Visual Studio 2022](https://visualstudio.microsoft.com/) or [VS Code](https://code.visualstudio.com/) with C# extension
- Install [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Clone and build as shown in the "Build from Source" section above

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- **[DuckDB](https://duckdb.org/)** - Fast in-process SQL database that powers Parquet operations
- **[Apache Parquet](https://parquet.apache.org/)** - Columnar storage format specification
- **[WPF](https://docs.microsoft.com/en-us/dotnet/desktop/wpf/)** - Microsoft's desktop UI framework
- Built with ❤️ to make working with Parquet files easier for everyone

## Support

- **Issues**: Found a bug? [Open an issue](https://github.com/jhew/HipHipParquet/issues)
- **Questions**: Have a question? Check existing issues or start a discussion
- **Feature Requests**: Got an idea? Open an issue with the "enhancement" label

---

**Hip Hip Parquet** - Making Parquet files accessible to everyone 🎉
