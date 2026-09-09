#!/usr/bin/env bash
set -euo pipefail

install_dir="${1:-$(cd "$(dirname "$0")/.." && pwd)}"
current_file="${install_dir}/current.txt"
port_file="${install_dir}/port.txt"
pid_file="${install_dir}/app.pid"

[[ -f "$current_file" ]] || { echo "WorkLens current.txt is missing." >&2; exit 1; }
[[ -f "$port_file" ]] || { echo "WorkLens port.txt is missing." >&2; exit 1; }
version="$(tr -d '\r\n' < "$current_file")"
port="$(tr -d '\r\n' < "$port_file")"
executable="${install_dir}/versions/${version}/WorkLens"
[[ -x "$executable" ]] || { echo "WorkLens executable is missing for version ${version}." >&2; exit 1; }

export ASPNETCORE_URLS="http://127.0.0.1:${port}"
export DOTNET_ENVIRONMENT="Production"

# launchd does not load interactive shell profiles, so Node installed by nvm
# is otherwise invisible to WorkLens and ask-bridge. Resolve nvm's default
# Node installation when it is available, without hard-coding a version.
nvm_dir="${NVM_DIR:-${HOME}/.nvm}"
nvm_node_bin=""
if [[ -s "${nvm_dir}/nvm.sh" ]]; then
  # shellcheck disable=SC1090
  . "${nvm_dir}/nvm.sh" --no-use
  nvm_node="$(nvm which default 2>/dev/null || true)"
  if [[ -x "$nvm_node" ]]; then
    nvm_node_bin="$(dirname "$nvm_node")"
  fi
fi

export PATH="${HOME}/.local/bin:${nvm_node_bin:+${nvm_node_bin}:}/opt/homebrew/bin:/usr/local/bin:${PATH:-/usr/bin:/bin:/usr/sbin:/sbin}"
app_pid=""
cleanup() { rm -f "$pid_file"; }
terminate() {
  trap - INT TERM
  if [[ "$app_pid" =~ ^[0-9]+$ ]]; then
    kill "$app_pid" >/dev/null 2>&1 || true
    wait "$app_pid" >/dev/null 2>&1 || true
  fi
  exit 0
}
trap cleanup EXIT
trap terminate INT TERM

cd "$(dirname "$executable")"
"$executable" &
app_pid=$!
printf '%s\n' "$app_pid" > "$pid_file"
wait "$app_pid"
