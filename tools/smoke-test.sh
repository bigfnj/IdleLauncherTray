#!/usr/bin/env bash
#
# Pre-release smoke test.
#
# Three things this script deliberately does NOT do any more:
#
#   * exit early when llvm-readobj is absent. The icon-resource assertion needs it,
#     nothing else does, and hard-failing meant the repo's only automated check could
#     not run at all on a machine without LLVM. It now degrades and says so, loudly,
#     on every run.
#   * grep the C# sources for string literals. "TargetFilePolicy.cs contains \".exe\""
#     passes just as happily when the code around that literal is unreachable. The
#     accepted-target behaviour is asserted by IdleLauncherTray.Tests instead, which
#     runs the real code.
#   * hardcode the version. It said 2.3.0 while the project was at 2.4.0, so the
#     publish folder it named was a lie. The version now comes from the .csproj.
#
# Set SMOKE_STRICT=1 to turn a degraded run into a failure (recommended for CI).

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

SOLUTION="IdleLauncherTray.sln"
CSPROJ="IdleLauncherTray/IdleLauncherTray.csproj"
POLICY_SOURCE="IdleLauncherTray/TargetFilePolicy.cs"
PUBLISH_PROFILE="IdleLauncherTray/Properties/PublishProfiles/IdleLauncherTray_v2_3_FrameworkDependent_SingleExe.pubxml"
# Overridable so the floor itself can be exercised: raise it and the run must fail.
MIN_POLICY_TESTS="${MIN_POLICY_TESTS:-10}"

SMOKE_STRICT="${SMOKE_STRICT:-0}"
DEGRADED_COUNT=0
DEGRADED_LIST=""

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

degrade() {
  DEGRADED_COUNT=$((DEGRADED_COUNT + 1))
  DEGRADED_LIST="${DEGRADED_LIST}  - SKIPPED: $1"$'\n'"      reason: $2"$'\n'
  echo "" >&2
  echo "############################################################" >&2
  echo "## WARNING: DEGRADED RUN -- a check was NOT performed" >&2
  echo "##   skipped: $1" >&2
  echo "##   reason:  $2" >&2
  echo "############################################################" >&2
  echo "" >&2
}

require_file_contains() {
  local file="$1"
  local needle="$2"

  grep -Fq "$needle" "$file" || fail "missing '$needle' in $file"
}

# ---------------------------------------------------------------- version

VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$CSPROJ" | head -n 1)"
[[ -n "$VERSION" ]] || fail "could not read <Version> from $CSPROJ"
echo "Project version: $VERSION"

PUBLISH_DIR="${1:-publish/smoke/IdleLauncherTray-v${VERSION}-win-x64-framework-dependent-singlefile}"

# ---------------------------------------------------------------- behaviour

# The accepted-target policy is checked by running it, not by grepping for it.
echo "== Building =="
dotnet build "$SOLUTION" -c Release

echo "== Running the test suite =="
dotnet test "$SOLUTION" -c Release --no-build || fail "dotnet test failed"

# A green suite proves nothing if the tests that cover the target policy have been
# deleted or renamed out of the run, so count them.
echo "== Confirming the target-policy tests actually ran =="
POLICY_OUTPUT="$(dotnet test "$SOLUTION" -c Release --no-build \
  --filter 'FullyQualifiedName~TargetFilePolicyTests' 2>&1)" || {
    echo "$POLICY_OUTPUT" >&2
    fail "the TargetFilePolicy tests did not pass"
  }

POLICY_PASSED="$(sed -n 's/.*Passed!.*Passed: *\([0-9]\+\).*/\1/p' <<<"$POLICY_OUTPUT" | head -n 1)"
[[ -n "$POLICY_PASSED" ]] || {
  echo "$POLICY_OUTPUT" >&2
  fail "could not read a passing-test count for TargetFilePolicyTests"
}
[[ "$POLICY_PASSED" -ge "$MIN_POLICY_TESTS" ]] \
  || fail "only $POLICY_PASSED TargetFilePolicy tests ran; expected at least $MIN_POLICY_TESTS"
echo "TargetFilePolicy assertions executed: $POLICY_PASSED"

# ---------------------------------------------------------------- docs

# The supported-extension list the user is shown lives in one const. The test suite
# asserts that const matches the set the code actually accepts; this asserts the README
# matches the const. Nothing is duplicated into this script.
DISPLAY="$(sed -n 's:.*SupportedExtensionsDisplay = "\(.*\)";.*:\1:p' "$POLICY_SOURCE" | head -n 1)"
[[ -n "$DISPLAY" ]] || fail "could not read SupportedExtensionsDisplay from $POLICY_SOURCE"

EXTENSIONS="$(grep -o '\.[a-z0-9]\+' <<<"$DISPLAY" || true)"
[[ -n "$EXTENSIONS" ]] || fail "SupportedExtensionsDisplay parsed as empty: '$DISPLAY'"

while read -r ext; do
  require_file_contains "README.md" "$ext"
done <<<"$EXTENSIONS"
echo "README documents every advertised extension: $(tr '\n' ' ' <<<"$EXTENSIONS")"

# ---------------------------------------------------------------- publish profile

require_file_contains "$PUBLISH_PROFILE" "<RuntimeIdentifier>win-x64</RuntimeIdentifier>"
require_file_contains "$PUBLISH_PROFILE" "<SelfContained>false</SelfContained>"
require_file_contains "$PUBLISH_PROFILE" "<PublishSingleFile>true</PublishSingleFile>"
require_file_contains "$PUBLISH_PROFILE" "<DebugType>embedded</DebugType>"
require_file_contains "$PUBLISH_PROFILE" "<DebugSymbols>true</DebugSymbols>"

# ---------------------------------------------------------------- publish

echo "== Publishing =="
rm -rf "$PUBLISH_DIR"

dotnet publish "$CSPROJ" \
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

[[ -f "$PUBLISH_DIR/IdleLauncherTray.exe" ]] \
  || fail "publish did not create $PUBLISH_DIR/IdleLauncherTray.exe"

if find "$PUBLISH_DIR" -name '*.pdb' -print -quit | grep -q .; then
  fail "publish output contains external .pdb files; debug symbols should be embedded"
fi

# ---------------------------------------------------------------- icon resources

if command -v llvm-readobj >/dev/null 2>&1; then
  RESOURCE_SUMMARY="$(llvm-readobj --coff-resources "$PUBLISH_DIR/IdleLauncherTray.exe")"

  grep -Fq "Type: ICON" <<<"$RESOURCE_SUMMARY" \
    || fail "published executable is missing embedded ICON resources"
  grep -Fq "Type: GROUP_ICON" <<<"$RESOURCE_SUMMARY" \
    || fail "published executable is missing an embedded GROUP_ICON resource"

  echo "Embedded ICON and GROUP_ICON resources verified."
else
  degrade "embedded ICON / GROUP_ICON resource check on $PUBLISH_DIR/IdleLauncherTray.exe" \
          "llvm-readobj is not on PATH (install LLVM, or run this on a machine that has it)"
fi

# ---------------------------------------------------------------- verdict

echo ""
if [[ "$DEGRADED_COUNT" -gt 0 ]]; then
  echo "************************************************************"
  echo "*  SMOKE TEST PASSED -- ** DEGRADED ** ($DEGRADED_COUNT check(s) not run)"
  echo "************************************************************"
  printf '%s' "$DEGRADED_LIST"
  echo "  This run did NOT verify the item(s) above. Do not read it as a"
  echo "  clean bill of health for them."
  echo "************************************************************"
  if [[ "$SMOKE_STRICT" != "0" ]]; then
    fail "SMOKE_STRICT is set and this run was degraded"
  fi
else
  echo "Smoke test passed. Every check ran."
fi

echo "Publish output: $PUBLISH_DIR"
