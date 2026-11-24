#!/usr/bin/env bash
# Builds a macOS IL2CPP player (high stripping) from the Persistent Shop sample plus a smoke runner, runs it,
# and checks that DI, generated proxies, transactions, repositories, the catalog, events, ticks, lifecycle,
# an enum-keyed strategy registry and the file store work there. Needs the Mac IL2CPP module and Xcode tools.
#
# Usage:
#   Tests~/il2cpp-smoke.sh [/path/to/Unity]
#
# UNITY_PATH may be set instead of the argument; UNISPRING_SMOKE_HOST chooses the project folder.

set -euo pipefail

PACKAGE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UNITY="${1:-${UNITY_PATH:-/Applications/Unity/Hub/Editor/6000.6.6f1/Unity.app/Contents/MacOS/Unity}}"
if [[ ! -x "$UNITY" ]]; then
    echo "Unity executable not found: $UNITY" >&2
    exit 2
fi

HOST="${UNISPRING_SMOKE_HOST:-$PACKAGE_ROOT/.ci/Il2CppSmoke}"
rm -rf "$HOST"
cp -R "$PACKAGE_ROOT/Tests~/UnityProject" "$HOST"
mkdir -p "$HOST/Assets/Shop"
cp -R "$PACKAGE_ROOT/Samples~/PersistentShop/Runtime" "$HOST/Assets/Shop/Runtime"
cp -R "$PACKAGE_ROOT/Samples~/PersistentShop/Resources" "$HOST/Assets/Shop/Resources"
cp -R "$PACKAGE_ROOT/Tests~/Il2CppSmoke/Assets/Smoke" "$HOST/Assets/Smoke"
cat > "$HOST/Packages/manifest.json" <<JSON
{
    "dependencies": {
        "com.ialex86.unispring": "file:$PACKAGE_ROOT",
        "com.unity.modules.imgui": "1.0.0",
        "com.unity.modules.jsonserialize": "1.0.0"
    }
}
JSON

echo "== 1/3 prepare (IL2CPP settings, scene, generated proxies)"
"$UNITY" -batchmode -nographics -quit -projectPath "$HOST" -buildTarget OSXUniversal \
    -executeMethod UniSpringSmoke.Editor.SmokeBuild.Prepare -logFile "$HOST/prepare.log" \
    || { echo "prepare failed, see $HOST/prepare.log" >&2; exit 1; }
ls "$HOST"/Assets/Shop/Runtime/UniSpring.Generated.cs >/dev/null \
    || { echo "no generated proxies in Assets/Shop/Runtime, see $HOST/prepare.log" >&2; exit 1; }

echo "== 2/3 build the IL2CPP player"
UNISPRING_SMOKE_OUTPUT="$HOST/Build/UniSpringSmoke.app" \
"$UNITY" -batchmode -nographics -quit -projectPath "$HOST" -buildTarget OSXUniversal \
    -executeMethod UniSpringSmoke.Editor.SmokeBuild.Player -logFile "$HOST/build.log" \
    || { echo "build failed, see $HOST/build.log" >&2; exit 1; }

echo "== 3/3 run it"
set +e
"$HOST/Build/UniSpringSmoke.app/Contents/MacOS/UniSpringSmoke" -batchmode -nographics -logFile "$HOST/run.log" &
PLAYER=$!
for _ in $(seq 1 120); do
    kill -0 "$PLAYER" 2>/dev/null || break
    sleep 1
done
if kill -0 "$PLAYER" 2>/dev/null; then
    echo "the player did not quit in 120 s; stopping it" >&2
    kill "$PLAYER"
fi
wait "$PLAYER"
CODE=$?
set -e
grep "SMOKE" "$HOST/run.log" || true
if [[ $CODE -ne 0 ]] || ! grep -q "UNISPRING SMOKE PASSED" "$HOST/run.log"; then
    echo "IL2CPP smoke test FAILED (exit $CODE), see $HOST/run.log" >&2
    exit 1
fi
echo "IL2CPP smoke test passed"
