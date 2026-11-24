#!/usr/bin/env bash
# Runs the package EditMode tests in a throw-away host project.
#
# Unity refuses to open a project path containing "~", so the template in
# Tests~/UnityProject is copied outside the package and its manifest is
# pointed at the absolute package path.
#
# Usage:
#   Tests~/run-tests.sh [/path/to/Unity]
#
# UNITY_PATH may also be set instead of the argument.

set -euo pipefail

PACKAGE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${1:-${UNITY_PATH:-}}"

if [[ -z "$UNITY" ]]; then
    UNITY="/Applications/Unity/Hub/Editor/6000.6.6f1/Unity.app/Contents/MacOS/Unity"
fi

if [[ ! -x "$UNITY" ]]; then
    echo "Unity executable not found: $UNITY" >&2
    echo "Pass it as the first argument or set UNITY_PATH." >&2
    exit 2
fi

HOST="${UNISPRING_TEST_HOST:-$(mktemp -d)/UniSpringTestProject}"
RESULTS="${UNISPRING_TEST_RESULTS:-$PACKAGE_ROOT/TestResults/editmode.xml}"
mkdir -p "$(dirname "$RESULTS")"

python3 "$PACKAGE_ROOT/Tests~/check-samples.py"

rm -rf "$HOST"
cp -R "$PACKAGE_ROOT/Tests~/UnityProject" "$HOST"
# The shop sample is compiled and its tests run with the package's.
mkdir -p "$HOST/Assets"
cp -R "$PACKAGE_ROOT/Samples~/PersistentShop" "$HOST/Assets/PersistentShop"

cat > "$HOST/Packages/manifest.json" <<EOF
{
    "dependencies": {
        "com.unity.test-framework": "1.8.0",
        "com.ialex86.unispring": "file:$PACKAGE_ROOT"
    },
    "testables": [
        "com.ialex86.unispring"
    ]
}
EOF

"$UNITY" \
    -batchmode \
    -nographics \
    -projectPath "$HOST" \
    -runTests \
    -testPlatform editmode \
    -testResults "$RESULTS" \
    -logFile "$HOST/unity-tests.log"

echo "Results: $RESULTS"
