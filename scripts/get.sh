#!/usr/bin/env bash
set -euo pipefail

repo="wengct/WorkLens"
version="${WORKLENS_VERSION:-latest}"
install_dir="${WORKLENS_INSTALL_DIR:-}"
bin_dir="${WORKLENS_BIN_DIR:-}"
port="${WORKLENS_PORT:-5077}"
autostart=1
open_browser=1

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="$2"; shift 2 ;;
    --install-dir) install_dir="$2"; shift 2 ;;
    --bin-dir) bin_dir="$2"; shift 2 ;;
    --port) port="$2"; shift 2 ;;
    --no-autostart) autostart=0; shift ;;
    --no-open-browser) open_browser=0; shift ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

if [[ ! "$port" =~ ^[0-9]+$ || "$port" -lt 1 || "$port" -gt 65535 ]]; then
  echo "Port must be an integer from 1 to 65535." >&2
  exit 2
fi

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This installer currently supports macOS. Use scripts/get.ps1 on Windows." >&2
  exit 1
fi

case "$(uname -m)" in
  arm64) target="osx-arm64" ;;
  x86_64) target="osx-x64" ;;
  *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 1 ;;
esac

if [[ "$version" == "latest" ]]; then
  echo "Resolving the latest WorkLens release..."
  latest_url="$(curl -fsSL -o /dev/null -w '%{url_effective}' "https://github.com/${repo}/releases/latest")"
  tag="${latest_url##*/}"
  if [[ -z "$tag" || "$tag" == "latest" ]]; then
    echo "The latest WorkLens release tag could not be resolved." >&2
    exit 1
  fi
else
  tag="$version"
fi

archive="worklens-${tag}-${target}.tar.gz"
release_base="https://github.com/${repo}/releases/download/${tag}"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/worklens.XXXXXX")"
trap 'rm -rf "$work_dir"' EXIT

echo "Downloading WorkLens ${tag}..."
curl -fsSL "${release_base}/${archive}" -o "${work_dir}/${archive}"
curl -fsSL "${release_base}/SHA256SUMS" -o "${work_dir}/SHA256SUMS"
expected="$(awk -v name="$archive" '$2 == name || $2 == "*" name { print $1; exit }' "${work_dir}/SHA256SUMS")"
if [[ -z "$expected" ]]; then
  echo "SHA256SUMS does not contain an entry for ${archive}." >&2
  exit 1
fi
actual="$(shasum -a 256 "${work_dir}/${archive}" | awk '{print $1}')"
if [[ "$actual" != "$expected" ]]; then
  echo "The WorkLens archive checksum did not match the published SHA-256 value." >&2
  exit 1
fi

package_dir="${work_dir}/package"
mkdir -p "$package_dir"
tar -xzf "${work_dir}/${archive}" -C "$package_dir"
install_script="${package_dir}/scripts/install.sh"
if [[ ! -x "$install_script" ]]; then
  chmod +x "$install_script" 2>/dev/null || true
fi
if [[ ! -f "$install_script" ]]; then
  echo "The release package does not contain scripts/install.sh." >&2
  exit 1
fi

args=(--port "$port")
[[ -n "$install_dir" ]] && args+=(--install-dir "$install_dir")
[[ -n "$bin_dir" ]] && args+=(--bin-dir "$bin_dir")
[[ "$autostart" -eq 0 ]] && args+=(--no-autostart)
[[ "$open_browser" -eq 0 ]] && args+=(--no-open-browser)
"$install_script" "${args[@]}"
