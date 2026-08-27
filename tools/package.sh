#!/usr/bin/env bash
# Builds the plugin, packages the release zip, and regenerates manifest.json with a matching
# checksum. Jellyfin verifies the MD5 on download, so the manifest and the zip must be produced
# together -- editing either by hand will make installs fail with a checksum error.
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="${1:-1.0.0.0}"
BRANCH="${2:-$(git rev-parse --abbrev-ref HEAD)}"
REPO="oliverinhalo/Jellyfin-resize"
ZIP="dist/media-optimizer_${VERSION}.zip"

echo "Building ${VERSION} for branch ${BRANCH}"
dotnet build -c Release --nologo

rm -rf dist/staging
mkdir -p dist/staging
cp "Jellyfin.Plugin.MediaOptimizer/bin/Release/net9.0/Jellyfin.Plugin.MediaOptimizer.dll" dist/staging/

rm -f "$ZIP"
( cd dist/staging && zip -q -X "../../${ZIP}" Jellyfin.Plugin.MediaOptimizer.dll )
rm -rf dist/staging

CHECKSUM="$(md5sum "$ZIP" | cut -d' ' -f1)"
TIMESTAMP="$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
SOURCE_URL="https://raw.githubusercontent.com/${REPO}/${BRANCH}/${ZIP}"

python3 - "$VERSION" "$CHECKSUM" "$TIMESTAMP" "$SOURCE_URL" <<'PY'
import json, sys, os

version, checksum, timestamp, source_url = sys.argv[1:5]

entry = {
    "version": version,
    "changelog": "Initial release.",
    "targetAbi": "10.11.0.0",
    "sourceUrl": source_url,
    "checksum": checksum,
    "timestamp": timestamp,
}

if os.path.exists("manifest.json"):
    with open("manifest.json") as f:
        manifest = json.load(f)
else:
    manifest = [{
        "guid": "919929b2-0857-4c61-8647-c05fbbdc62fa",
        "name": "Media Optimizer",
        "description": "Inspect any media file from inside Jellyfin and convert it with FFmpeg.",
        "overview": "Inspect and convert media files from inside the Jellyfin interface.",
        "owner": "oliverinhalo",
        "category": "General",
        "versions": [],
    }]

versions = [v for v in manifest[0]["versions"] if v["version"] != version]
manifest[0]["versions"] = [entry] + versions

with open("manifest.json", "w") as f:
    json.dump(manifest, f, indent=4)
    f.write("\n")
PY

echo
echo "Packaged  ${ZIP}"
echo "Checksum  ${CHECKSUM}"
echo "Source    ${SOURCE_URL}"
echo
echo "Commit and push dist/ and manifest.json, then the repository URL is:"
echo "  https://raw.githubusercontent.com/${REPO}/${BRANCH}/manifest.json"
