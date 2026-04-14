#!/usr/bin/env bash
# Install Webull Analytics
# Builds the project and copies the executable to the install directory.
# Adds the install directory to the user's PATH if not already present.
#
# Usage:
#   ./install.sh              - installs to ~/.local/bin
#   ./install.sh /custom/dir  - installs to the specified directory

set -euo pipefail

INSTALL_DIR="${1:-$HOME/.local/bin}"

echo "============================================"
echo " Webull Analytics Installer (Linux)"
echo "============================================"
echo
echo "Install directory: $INSTALL_DIR"
echo

# Check if dotnet is installed
if ! command -v dotnet &>/dev/null; then
    echo "ERROR: .NET SDK is not installed!"
    echo "Please download and install .NET 10.0 SDK from:"
    echo "https://dotnet.microsoft.com/download"
    exit 1
fi

echo "Building self-contained executable..."
echo

dotnet publish -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true

echo
echo "Build succeeded."
echo

# Create install directory if it doesn't exist
mkdir -p "$INSTALL_DIR"

# Copy the executable
echo "Copying WebullAnalytics to $INSTALL_DIR..."
cp -f "bin/Release/net10.0/linux-x64/publish/WebullAnalytics" "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/WebullAnalytics"

# Create data directory if it doesn't exist
mkdir -p "$INSTALL_DIR/data"

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
echo "You can now run: WebullAnalytics"
echo
