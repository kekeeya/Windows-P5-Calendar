#!/bin/bash
# Builds the folder a Windows user actually runs, and zips it.
#
# Runs anywhere .NET 8 does, macOS included — the SDK cross-publishes a real
# Windows executable, which is what lets the whole thing be built and checked on
# the machine that has the macOS renderer to compare against.
#
#     tools/publish.sh [win-x64|win-arm64]
#
# The output is the whole `dist/Mona` directory, not just the exe: the art pack
# sits beside it and so do the runtime's native libraries.

set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runtime="${1:-win-x64}"
version="0.1.0"

cd "$here"
rm -rf "dist/Mona" "dist/Mona-$version-$runtime.zip"

dotnet publish src/Mona.App \
    -c Release \
    -r "$runtime" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:DebugType=none \
    -o "dist/Mona" \
    --nologo

# A missing art pack is not a build error, so it is checked rather than assumed:
# the app starts perfectly well without it and then cannot draw anything, which
# is a poor thing to discover on someone else's machine.
for required in \
    assets/CalendarArt/cal-layout.json \
    assets/CalendarArt/cal-cities.tsv \
    assets/Tray/MonaHead.png
do
    if [ ! -f "dist/Mona/$required" ]; then
        echo "the published art pack is missing $required" >&2
        exit 1
    fi
done

count=$(find dist/Mona/assets/CalendarArt -name '*.png' | wc -l | tr -d ' ')
if [ "$count" -lt 100 ]; then
    echo "the published art pack is incomplete: only $count calendar PNGs" >&2
    exit 1
fi

(cd dist && zip -qr "Mona-$version-$runtime.zip" Mona)

echo
echo "dist/Mona                          $(du -sh dist/Mona | cut -f1)"
echo "dist/Mona-$version-$runtime.zip    $(du -sh "dist/Mona-$version-$runtime.zip" | cut -f1)"
echo "art pack: $count calendar PNGs, 1 tray icon"
