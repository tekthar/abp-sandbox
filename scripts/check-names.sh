#!/usr/bin/env bash
# Checks that no former host's name is in what the repository ships (docs/adr/0001):
# sources, project files, docs and scripts; given --packages <dir>, also every file inside the .nupkg files there
# (assemblies' strings, the nuspec, the readme). CHANGELOG.md may name it, to tell clients what was renamed. Pack as
# CI does (ContinuousIntegrationBuild), or the checkout's own path is in the assemblies. Exits 1 with each line found.
set -euo pipefail
cd "$(dirname "$0")/.."

pattern='\b[Tt]oda\b|TODA|Toda[A-Z.]|toda-|T\x00o\x00d\x00a\x00[.A-Z ]'

packages=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --packages) packages="${2:-}"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

found=$(git ls-files -z -- . ':!CHANGELOG.md' ':!scripts/check-names.sh' \
  | xargs -0 grep -nIP "$pattern" -- 2>/dev/null || true)

if [[ -n "$packages" ]]; then
  shopt -s nullglob
  nupkgs=("$packages"/*.nupkg)
  if [[ ${#nupkgs[@]} -eq 0 ]]; then echo "no .nupkg in $packages" >&2; exit 2; fi
  work=$(mktemp -d)
  trap 'rm -rf "$work"' EXIT
  for n in "${nupkgs[@]}"; do
    dir="$work/$(basename "$n")"
    mkdir -p "$dir" && unzip -qo "$n" -d "$dir"
    # -a reads assemblies as text; UTF-16 user strings are matched by the pattern's second half.
    hits=$(grep -rlaP "$pattern" "$dir" | sed "s|^$dir/|$(basename "$n"): |" || true)
    found+="${hits:+$'\n'$hits}"
  done
fi

found=$(printf '%s' "$found" | sed '/^$/d')
if [[ -n "$found" ]]; then
  echo "A former host's name is back (docs/adr/0001):" >&2
  echo "$found" >&2
  exit 1
fi
echo "names: clean"
