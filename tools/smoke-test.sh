#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

VERSION="2.3.0"
PUBLISH_DIR="${1:-publish/smoke/IdleLauncherTray-v${VERSION}-win-x64-framework-dependent-singlefile}"
PUBLISH_PROFILE="IdleLauncherTray/Properties/PublishProfiles/IdleLauncherTray_v2_3_FrameworkDependent_SingleExe.pubxml"
SUPPORTED_EXTENSIONS=(".exe" ".scr" ".bat" ".cmd" ".lnk" ".msi" ".ps1" ".vbs" ".jar" ".py")

require_file_contains() {
  local file="$1"
  local needle="$2"

  if ! grep -Fq "$needle" "$file"; then
    echo "Missing '$needle' in $file" >&2
    exit 1
  fi
}

for ext in "${SUPPORTED_EXTENSIONS[@]}"; do
  require_file_contains "IdleLauncherTray/TargetFilePolicy.cs" "\"$ext\""
  require_file_contains "README.md" "$ext"
done

require_file_contains "$PUBLISH_PROFILE" "<RuntimeIdentifier>win-x64</RuntimeIdentifier>"
require_file_contains "$PUBLISH_PROFILE" "<SelfContained>false</SelfContained>"
require_file_contains "$PUBLISH_PROFILE" "<PublishSingleFile>true</PublishSingleFile>"
require_file_contains "$PUBLISH_PROFILE" "<DebugType>embedded</DebugType>"
require_file_contains "$PUBLISH_PROFILE" "<DebugSymbols>true</DebugSymbols>"

if ! command -v llvm-readobj >/dev/null 2>&1; then
  echo "llvm-readobj is required for icon-resource smoke testing." >&2
  exit 1
fi

dotnet build IdleLauncherTray.sln

rm -rf "$PUBLISH_DIR"

dotnet publish IdleLauncherTray/IdleLauncherTray.csproj \
  -c Release \
  -r win-x64 \
  --self-contained false \
  -p:PublishSingleFile=true \
  -p:UseAppHost=true \
  -p:EnableCompressionInSingleFile=false \
  -p:PublishReadyToRun=false \
  -p:PublishTrimmed=false \
  -p:DebugType=embedded \
  -p:DebugSymbols=true \
  -o "$PUBLISH_DIR"

if [[ ! -f "$PUBLISH_DIR/IdleLauncherTray.exe" ]]; then
  echo "Publish did not create $PUBLISH_DIR/IdleLauncherTray.exe" >&2
  exit 1
fi

if find "$PUBLISH_DIR" -name '*.pdb' -print -quit | grep -q .; then
  echo "Publish output should not contain external .pdb files; debug symbols should be embedded." >&2
  exit 1
fi

RESOURCE_SUMMARY="$(llvm-readobj --coff-resources "$PUBLISH_DIR/IdleLauncherTray.exe")"

if ! grep -Fq "Type: ICON" <<<"$RESOURCE_SUMMARY"; then
  echo "Published executable is missing embedded ICON resources." >&2
  exit 1
fi

if ! grep -Fq "Type: GROUP_ICON" <<<"$RESOURCE_SUMMARY"; then
  echo "Published executable is missing an embedded GROUP_ICON resource." >&2
  exit 1
fi

echo "Smoke test passed. Publish output: $PUBLISH_DIR"
