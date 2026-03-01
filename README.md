# Hip Hip Parquet

A Windows desktop application for viewing, editing, and analyzing Apache Parquet files with integrated data quality assessment tools.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![WPF](https://img.shields.io/badge/UI-WPF-blue)
![License](https://img.shields.io/badge/license-MIT-green)

## Overview

Hip Hip Parquet is a desktop application for working with Apache Parquet files on Windows. It provides file viewing, editing, and comprehensive data quality analysis capabilities designed for data analysts, engineers, and quality assurance professionals.

Apache Parquet is a columnar storage format widely used in data engineering and analytics workflows with tools such as Apache Spark, Pandas, and cloud data warehouses.

## Features

### File Operations
- **Open Parquet Files** - Load `.parquet` files via file picker or Windows Explorer context menu
- **Edit & Save** - Modify cell values with inline editing and save changes in Parquet format
- **Save As** - Create new files from modified data
- **Recent Files** - Access the 10 most recently opened files
- **Unsaved Changes Protection** - Warning prompts before closing unsaved work

### Data Exploration
- **Persistent Row Numbers** - Maintain row indexing through sorting and filtering operations
- **Sortable Columns** - Sort by any column in ascending or descending order
- **Column Filters** - Apply independent search filters to individual columns
- **Global Search** - Search across all columns simultaneously
- **Schema Viewer** - Display file metadata, column names, data types, and row counts
- **Copy to Clipboard** - Export selected cells as CSV or TSV format

### Data Quality Analysis
- **QA Review Panel** - Comprehensive data quality assessment interface
- **Quality Scoring System** - Four-dimensional scoring (Completeness, Uniqueness, Validity, Distribution)
- **Statistical Profiling** - Per-column analysis including null rates, distinct counts, outliers, and distribution metrics
- **Narrative Findings** - Automated detection and reporting of data quality issues
- **Group-By Analysis** - Dimensional breakdown of quality metrics by categorical columns
- **File Comparison** - Schema and data drift detection between file versions
- **HTML Export** - Generate self-contained quality reports with visualizations

### User Interface
- **Resizable Layout** - Adjust column widths and panel sizes
- **Collapsible Sections** - Toggle visibility of schema pane and filter controls
- **Virtualized Scrolling** - Efficient rendering for large datasets
- **Error Handling** - Clear error messages and graceful failure handling

## Installation

### Pre-built Release
1. Download the latest release from the [Releases](https://github.com/jhew/HipHipParquet/releases) page
2. Extract the `.zip` file
3. Run `HipHipParquet.exe`

### Build from Source

**Prerequisites:**
- Windows 10 or Windows 11
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

**Build Instructions:**
```powershell
git clone https://github.com/jhew/HipHipParquet.git
cd HipHipParquet
dotnet build
dotnet run
```

## Usage

### Basic Operations

**Opening Files**
- Select **File → Open** and choose a `.parquet` file
- Use **File → Recent Files** for quick access to recent files
- Right-click any `.parquet` file in Windows Explorer and select "Open with Hip Hip Parquet"

**Data Navigation**
- Scroll through rows and columns using standard scroll controls
- Click column headers to sort data
- Use column-specific search boxes to filter data
- Use global search (lower right) to search across all columns

**Editing Data**
- Double-click any cell to enter edit mode
- Press Enter to confirm or Escape to cancel
- Unsaved changes are indicated by an asterisk in the window title

**Saving Changes**
- Press **Ctrl+S** or select **File → Save** to save modifications
- Use **File → Save As** to create a new file with changes
- Confirmation prompts appear when closing with unsaved changes

**Copying Data**
- Select one or more cells
- Use **Edit → Copy** or **Copy as CSV/TSV** to export to clipboard
- Paste into external applications such as Excel or text editors

**View Customization**
- Toggle **View → Schema Pane** to show or hide file metadata
- Toggle **View → Filter Row** to show or hide search controls
- Drag column borders to adjust width
- Drag the schema pane divider to resize panels

### Quality Analysis

**Accessing the QA Review Panel**
1. Open a Parquet file
2. The QA Review Panel appears on the right side of the window
3. Click **Analyze** to begin quality assessment

**Quality Scoring**

The system evaluates data quality across four dimensions (0-25 points each):
- **Completeness**: Measures missing values (0% nulls = 25 points)
- **Uniqueness**: Evaluates value diversity and cardinality
- **Validity**: Assesses type consistency and value correctness
- **Distribution**: Analyzes data spread, skewness, and outliers

Total scores range from 0-100 with color-coded grades:
- 80-100: Good (Green)
- 60-79: Fair (Yellow)
- Below 60: Needs Review (Red)

**Column Profiling**

View detailed statistics for each column:
- Null counts and percentages
- Distinct value counts
- Numeric statistics (min, max, mean, median, standard deviation, quartiles)
- String metrics (length statistics, empty string counts)
- Outlier detection (values beyond 1.5× IQR)
- Top value frequencies with percentages
- Distribution histograms and sparklines

**Narrative Findings**

Automated detection reports include:
- File overview with type breakdown and quality summary
- Best and worst performing columns
- Null rate analysis across all columns
- Dominant value warnings
- Outlier alerts
- Empty string detection
- Low cardinality identification
- Per-column quality scores

**Dimensional Analysis**

Group data by categorical columns:
1. Click **Group By Dimensions** to expand the section
2. Select one or more categorical columns (string columns with ≤100 distinct values)
3. Click **Apply Group By**
4. View quality scores and row counts for each group
5. Click **Clear** to reset dimension selection

**File Comparison**

Compare two versions of a file:
1. Analyze the baseline file
2. Click **Compare with another file**
3. Select the comparison file
4. Review schema changes (added, removed, or type-changed columns)
5. View column-level drift metrics (null rates, means, quality scores)
6. Examine the computed drift score (0 = identical, 100 = completely different)

**HTML Report Export**

Generate comprehensive quality reports:
1. Complete an analysis in the QA Review Panel
2. Click **Export** in the panel header
3. Choose a save location for the `.html` file
4. The report opens automatically in your default browser

**HTML Report Contents:**
- File summary with row count, column count, and completeness metrics
- Overall quality score with gauge visualization and dimensional breakdown
- Scoring key explaining each quality dimension
- Narrative findings list with severity indicators
- Column profiles table with scores, statistics, and distribution visualizations
- File comparison details (if comparison was performed)
- All visualizations are self-contained using inline SVG and CSS

**Filter and Query Builder**

Apply metric-based filters to column profiles:
1. Select a metric (Null %, Quality Score, Distinct Count, Outlier %)
2. Choose an operator (>, <, =, >=, <=)
3. Enter a threshold value
4. Click **Go**
5. View filtered columns; click **Clear Filter** to reset



## Architecture

### Technology Stack
- **Framework**: .NET 8.0 + WPF (Windows Presentation Foundation)
- **Data Engine**: DuckDB.NET for Parquet I/O operations
- **MVVM Framework**: CommunityToolkit.Mvvm for quality analysis features
- **Architecture**: Mixed code-behind (main window) and MVVM (QA panel) patterns
- **Target Platform**: Windows 10/11 x64

### Project Structure

```
HipHipParquet/
├── Assets/                        # Application resources
│   └── app.ico
├── Controls/                      # Custom WPF controls
│   ├── QualityGaugeControl.xaml   # Semicircular score gauge
│   └── SparklineControl.xaml      # Histogram visualization
├── Models/                        # Data models
│   ├── ColumnProfile.cs           # Column statistics and quality metrics
│   ├── FileProfile.cs             # File-level aggregated profile
│   ├── QualityScore.cs            # Four-dimensional quality scoring
│   ├── NarrativeItem.cs           # Finding/anomaly detection results
│   └── FileComparison.cs          # Schema and drift comparison
├── Services/                      # Business logic layer
│   ├── ParquetService.cs          # DuckDB integration for Parquet I/O
│   ├── QualityScoreService.cs     # Quality scoring algorithms
│   ├── NarrativeService.cs        # Rule-based findings generator
│   └── ReportService.cs           # HTML report generation
├── ViewModels/                    # MVVM view models
│   └── QaReviewViewModel.cs       # QA panel state and commands
├── Views/                         # User interface
│   ├── MainWindow.xaml            # Primary application window
│   └── QaReviewPanel.xaml         # Quality analysis panel
└── App.xaml(.cs)                  # Application entry and DI configuration
```

### Key Implementation Details

**Parquet Operations**
- DuckDB `read_parquet()` function for efficient file reading
- `COPY TO` command for Parquet file writing
- In-memory SQL execution for profiling and aggregation

**Quality Scoring Algorithm**
- Completeness: Linear scale based on null percentage (0% nulls = 25/25)
- Uniqueness: Distinct value ratio with context-aware thresholds
- Validity: Type consistency checks, empty string detection, range validation
- Distribution: Outlier analysis using 1.5× IQR method, skewness detection

**Statistical Profiling**
- Aggregates: MIN, MAX, AVG, MEDIAN, STDDEV, SUM
- Quartiles: Q1, Q3, IQR calculation
- Histogram generation: WIDTH_BUCKET function with 10 bins
- Top values: Frequency analysis with percentage calculation

**Performance Optimization**
- WPF DataGrid virtualization for large datasets
- Async/await patterns for non-blocking UI operations
- DuckDB connection pooling and query optimization
- Selective column profiling based on data type

**UI Components**
- Custom SVG gauge control with stroke-dasharray technique
- Dynamically bound sparkline histograms
- Collapsible sections with style triggers
- ObservableCollection data binding with INotifyPropertyChanged



## Contributing

Contributions are welcome. Submit bug fixes, feature additions, or documentation improvements through pull requests.

### Contribution Process
1. Fork the repository
2. Create a feature branch: `git checkout -b feature/YourFeature`
3. Implement changes with appropriate testing
4. Commit with descriptive messages: `git commit -m 'Add feature: YourFeature'`
5. Push to your fork: `git push origin feature/YourFeature`
6. Open a pull request with a detailed description

### Development Environment
- [Visual Studio 2022](https://visualstudio.microsoft.com/) or [VS Code](https://code.visualstudio.com/) with C# extension
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Follow the build instructions in the Installation section

## License

Licensed under the MIT License. See the [LICENSE](LICENSE) file for full details.

## Acknowledgments

- **[DuckDB](https://duckdb.org/)** - In-process SQL database engine for Parquet operations
- **[Apache Parquet](https://parquet.apache.org/)** - Columnar storage format specification
- **[WPF](https://docs.microsoft.com/en-us/dotnet/desktop/wpf/)** - Windows desktop UI framework

## Support

- **Bug Reports**: [Open an issue](https://github.com/jhew/HipHipParquet/issues) on GitHub
- **Feature Requests**: Submit issues with the "enhancement" label
- **Questions**: Review existing issues or start a new discussion

