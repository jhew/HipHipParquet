# Hip Hip Parquet Project

This is a .NET 10 WinUI 3 desktop application for viewing and editing Parquet files on Windows.

## Project Overview
- **Target**: Windows 11 x64 desktop application
- **Framework**: .NET 10 + WinUI 3 (Windows App SDK)
- **Data Engine**: DuckDB for efficient Parquet file operations
- **Packaging**: MSIX for Microsoft Store distribution
- **Target Users**: Data analysts and business users working with Parquet files up to 1GB

## Key Features (V1)
- View Parquet files with virtualized data grid
- Type-safe cell editing with undo/redo
- Schema viewer with column type information
- Filtering and search capabilities
- Save/Save As functionality
- Modern Windows 11 UI with proper file associations

## Development Guidelines
- Use MVVM pattern with CommunityToolkit.Mvvm
- Implement virtualized UI for large datasets
- Follow Windows App SDK best practices
- Ensure MSIX packaging compatibility
- Maintain type safety for data operations