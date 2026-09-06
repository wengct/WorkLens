#!/usr/bin/env bash
set -euo pipefail

package_dir="${1:?Usage: stage-leak-hunter.sh PACKAGE_DIR RID}"
rid="${2:?Usage: stage-leak-hunter.sh PACKAGE_DIR RID}"
[[ "$rid" == "osx-x64" || "$rid" == "osx-arm64" ]] || { echo "Unsupported leak-hunter RID: $rid" >&2; exit 2; }
version="$(tr -d '\r\n' < "$(dirname "$0")/leak-hunter.version")"
case "$rid" in
  osx-x64) asset="leak-hunter-x86_64-apple-darwin.tar.xz" ;;
  osx-arm64) asset="leak-hunter-aarch64-apple-darwin.tar.xz" ;;
esac
base_url="https://github.com/doggy8088/leak-hunter/releases/download/${version}"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/worklens-leak-hunter.XXXXXX")"
archive="${work_dir}/${asset}"
destination="${package_dir}/tools/leak-hunter"
cleanup() { rm -rf "$work_dir"; }
trap cleanup EXIT
mkdir -p "$destination"
curl -fsSL "${base_url}/${asset}" -o "$archive"
curl -fsSL "${base_url}/${asset}.sha256" -o "${archive}.sha256"
expected="$(awk '{print tolower($1)}' "${archive}.sha256")"
actual="$(shasum -a 256 "$archive" | awk '{print tolower($1)}')"
[[ "$expected" == "$actual" ]] || { echo "leak-hunter archive checksum mismatch." >&2; exit 1; }
tar -xJf "$archive" -C "$work_dir"
binary="$(find "$work_dir" -type f -name leak-hunter -perm -111 -print -quit)"
[[ -n "$binary" ]] || { echo "The leak-hunter macOS executable was not found in the verified archive." >&2; exit 1; }
cp "$binary" "$destination/leak-hunter"
chmod +x "$destination/leak-hunter"
version_output="$($destination/leak-hunter --version 2>/dev/null || true)"
expected_version_number="${version#v}"
expected_version_pattern="${expected_version_number//./\\.}"
printf '%s\n' "$version_output" | grep -Eiq "(^|[^[:alnum:].-])v?${expected_version_pattern}([^[:alnum:].-]|$)" || {
  echo "The verified leak-hunter executable reported an unexpected version: ${version_output}" >&2
  exit 1
}
cp "$(dirname "$0")/../third-party/leak-hunter-LICENSE.txt" "$destination/LICENSE.txt"
