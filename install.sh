#!/usr/bin/env bash
# Install wa (Webull Analytics)
# Builds the project, copies the executable to a directory on PATH, and seeds
# the data directory at the XDG location the binary looks for at startup.
#
# Layout (XDG-compliant):
#   ~/.local/bin/wa                            - executable (on PATH)
#   ~/.local/bin/wa-scraper                    - chain-snapshot scraper (on PATH)
#   ~/.local/share/WebullAnalytics/data/       - configs, history, intraday, etc.
#
# Program.BaseDir resolves to ~/.local/share/WebullAnalytics when its data/
# subdir exists, so this layout makes the binary read the right config
# regardless of which copy of wa the user happens to invoke.
#
# Usage:
#   ./install.sh                  - installs exe to ~/.local/bin
#   ./install.sh /custom/bindir   - installs exe to /custom/bindir, data still at ~/.local/share/...

set -euo pipefail

INSTALL_DIR="${1:-$HOME/.local/bin}"
DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/WebullAnalytics/data"

echo "============================================"
echo " wa (Webull Analytics) Installer (Linux)"
echo "============================================"
echo
echo "Executable:  $INSTALL_DIR/wa"
echo "Scraper:     $INSTALL_DIR/wa-scraper"
echo "Data dir:    $DATA_DIR"
echo

# Check if dotnet is installed
if ! command -v dotnet &>/dev/null; then
    echo "ERROR: .NET SDK is not installed!"
    echo "Please download and install .NET 10.0 SDK from:"
    echo "https://dotnet.microsoft.com/download"
    exit 1
fi

echo "Building self-contained executables..."
echo

dotnet publish WebullAnalytics.csproj -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true
dotnet publish WebullAnalytics.Scraper/WebullAnalytics.Scraper.csproj -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true

echo
echo "Build succeeded."
echo

# Create install directory if it doesn't exist
mkdir -p "$INSTALL_DIR"

# Copy the executables. cp -f directly onto a RUNNING binary fails with ETXTBSY (a watch or the
# scraper capturing until 16:05 holds the text segment), so copy to a temp name and rename over the
# target instead: rename is atomic, always succeeds, and the running process keeps executing its
# (now-unlinked) old inode untouched. Same idea as install.bat's move-aside on Windows, minus the
# .old leftovers — the unlinked inode is reclaimed when the old process exits. Running processes
# still execute the OLD code until restarted; the install just stops being blocked by them.
echo "Copying wa to $INSTALL_DIR..."
cp -f "bin/Release/net10.0/linux-x64/publish/wa" "$INSTALL_DIR/wa.new"
chmod +x "$INSTALL_DIR/wa.new"
mv -f "$INSTALL_DIR/wa.new" "$INSTALL_DIR/wa"

echo "Copying wa-scraper to $INSTALL_DIR..."
cp -f "WebullAnalytics.Scraper/bin/Release/net10.0/linux-x64/publish/wa-scraper" "$INSTALL_DIR/wa-scraper.new"
chmod +x "$INSTALL_DIR/wa-scraper.new"
mv -f "$INSTALL_DIR/wa-scraper.new" "$INSTALL_DIR/wa-scraper"

# Create the data dir at the XDG location the binary looks for at startup.
echo "Creating data directory at $DATA_DIR..."
mkdir -p "$DATA_DIR"

# Publish the daily data-refresh script + its Python helpers into the data folder so the scheduled
# refresh runs self-contained from the prod location (not the repo checkout). daily_backfill.sh
# resolves these by its own path and the store via WA_DATA_DIR, so it works from $DATA_DIR/scripts.
SCRIPTS_DIR="$DATA_DIR/scripts"
echo "Publishing data-refresh scripts to $SCRIPTS_DIR..."
mkdir -p "$SCRIPTS_DIR"
cp -f scripts/daily_backfill.sh scripts/backfill_thetadata.py scripts/import_quotes_sqlite.py "$SCRIPTS_DIR/"
chmod +x "$SCRIPTS_DIR/daily_backfill.sh"

# Add install directory to PATH if not already present
if echo ":$PATH:" | grep -q ":$INSTALL_DIR:"; then
    echo "Install directory is already in PATH."
else
    echo "Adding $INSTALL_DIR to PATH..."

    SHELL_NAME="$(basename "$SHELL")"
    case "$SHELL_NAME" in
        zsh)  RC_FILE="$HOME/.zshrc" ;;
        bash) RC_FILE="$HOME/.bashrc" ;;
        fish)
            fish -c "fish_add_path $INSTALL_DIR" 2>/dev/null && echo "PATH updated via fish_add_path." || echo "WARNING: Failed to update fish PATH."
            RC_FILE=""
            ;;
        *)    RC_FILE="$HOME/.profile" ;;
    esac

    if [ -n "$RC_FILE" ]; then
        echo "export PATH=\"$INSTALL_DIR:\$PATH\"" >> "$RC_FILE"
        echo "PATH updated in $RC_FILE. Restart your terminal for changes to take effect."
    fi
fi

echo
echo "============================================"
echo " Installation complete!"
echo "============================================"
echo
echo "You can now run: wa"
echo "Capture a day's chain snapshots with: wa-scraper SPXW"
echo
