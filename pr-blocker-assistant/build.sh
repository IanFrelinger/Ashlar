#!/usr/bin/env bash
set -euo pipefail

# Build script for PR Blocker Assistant
# Consumes Ashlar 0.3.0 from nuget.org only

echo "PR Blocker Assistant - Build"
echo "============================"
echo ""

# Check .NET SDK
if ! command -v dotnet &> /dev/null; then
    echo "ERROR: .NET SDK not found"
    echo "Install from: https://dot.net"
    exit 1
fi

echo "Restoring packages from nuget.org..."
dotnet restore --configfile nuget.config

echo ""
echo "Building solution..."
dotnet build --configuration Release --no-restore

echo ""
echo "Build complete!"
echo ""
echo "Run the tool:"
echo "  dotnet run --project src/PrBlocker.CLI --no-build --configuration Release -- <github-url>"
