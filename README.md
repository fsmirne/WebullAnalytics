# Webull Analytics (C# Version)

This is a C# port of the `cli.py` Python script that analyzes trading data from Webull CSV exports and generates realized P&L reports.

## Features

- Parses Webull CSV order exports for stocks and options
- Tracks positions using lot-based FIFO accounting
- Calculates realized P&L for closed positions
- Handles option expirations automatically
- Supports multi-leg option strategies (Iron Condors, Butterflies, Spreads, etc.)
- Displays formatted tables with transaction history and open positions

## Requirements

- .NET 10.0 SDK or later

## Dependencies

- **Spectre.Console** - For Rich-like table formatting
- **CsvHelper** - For CSV parsing

## Building

```bash
cd WebullAnalytics
dotnet build
```

## Running

```bash
dotnet run -- [options]
```

### Options

- `--data-dir <path>` - Directory containing CSV order exports (default: `data`)
- `--as-of <date>` - Include expirations on or before this date in YYYY-MM-DD format (default: today)
- `--help`, `-h` - Show help message

### Examples

```bash
# Use default data directory (./data) and today's date
dotnet run

# Specify custom data directory
dotnet run -- --data-dir C:\trading\exports

# Specify as-of date for expiration calculation
dotnet run -- --as-of 2024-12-31

# Both options
dotnet run -- --data-dir ../data --as-of 2024-12-31
```

## Publishing

To create a standalone executable:

```bash
# Windows
dotnet publish -c Release -r win-x64 --self-contained

# Linux
dotnet publish -c Release -r linux-x64 --self-contained

# macOS
dotnet publish -c Release -r osx-x64 --self-contained
```

The executable will be in `bin/Release/net10.0/<runtime>/publish/`

## Project Structure

- **Models.cs** - Data models (Trade, Lot, ReportRow, PositionRow, etc.)
- **ParsingHelpers.cs** - CSV parsing utilities
- **Formatters.cs** - Output formatting functions
- **CsvParser.cs** - CSV file parsing logic
- **PositionTracker.cs** - Position tracking and P&L calculation (lot-based FIFO)
- **TableRenderer.cs** - Table rendering with Spectre.Console
- **Program.cs** - Main entry point and CLI argument parsing

## Differences from Python Version

1. Manual command-line argument parsing instead of Click library
2. Uses Spectre.Console instead of Rich for table formatting
3. Uses CsvHelper for CSV parsing
4. All core logic and calculations remain identical
