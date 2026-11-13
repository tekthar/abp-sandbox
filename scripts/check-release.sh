#!/usr/bin/env bash
# Checks that the version is ready to release: CHANGELOG.md has a "## [<version>]" entry; given --tag v<version>, that
# the tag names the version in Directory.Build.props; given --unpublished, that nuget.org doesn't have AbpSandbox at
# that version yet (nuget.org refuses a duplicate push, but only after the other packages went). Exits 1 with the reason
# otherwise.
set -euo pipefail
cd "$(dirname "$0")/.."

prefix=$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' Directory.Build.props)
suffix=$(sed -n 's:.*<VersionSuffix>\(.*\)</VersionSuffix>.*:\1:p' Directory.Build.props)
version="$prefix${suffix:+-$suffix}"
problems=()

tag=""
check_registry=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --tag) tag="${2:-}"; shift 2 ;;
    --unpublished) check_registry=1; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [[ -n "$tag" && "$tag" != "v$version" ]]; then
  problems+=("the tag is \"$tag\" but Directory.Build.props says $version: tag v$version")
fi

if ! grep -qF "## [$version]" CHANGELOG.md; then
  problems+=("CHANGELOG.md has no \"## [$version]\" entry")
fi

if [[ $check_registry == 1 ]]; then
  versions=$(mktemp)
  status=$(curl -s -o "$versions" -w '%{http_code}' "https://api.nuget.org/v3-flatcontainer/abpsandbox/index.json" || true)
  lower=$(printf '%s' "$version" | tr '[:upper:]' '[:lower:]')
  if [[ "$status" == "200" ]]; then
    if grep -qF "\"$lower\"" "$versions"; then
      problems+=("AbpSandbox $version is already on nuget.org: bump the version")
    fi
  elif [[ "$status" != "404" ]]; then
    problems+=("can't ask nuget.org for AbpSandbox's versions (HTTP $status)")
  fi
  rm -f "$versions"
fi

if [[ ${#problems[@]} -gt 0 ]]; then
  for p in "${problems[@]}"; do echo "AbpSandbox $version: $p" >&2; done
  exit 1
fi
echo "AbpSandbox $version: ready to release"
