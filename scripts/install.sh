#!/usr/bin/env bash
set -euo pipefail

install_dir="${HOME}/.local/share/worklens"
bin_dir="${HOME}/.local/bin"
port=5077
autostart=1
open_browser=1

while [[ $# -gt 0 ]]; do
  case "$1" in
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

data_dir="${HOME}/Library/Application Support/WorkLens"
if [[ -z "$install_dir" || "$install_dir" == "/" || "$install_dir" == "$HOME" || "$install_dir" == "$data_dir" || ${#install_dir} -lt 8 ]]; then
  echo "Refusing to install WorkLens into an unsafe or runtime-data path: ${install_dir}" >&2
  exit 1
fi

script_dir="$(cd "$(dirname "$0")" && pwd)"
release_dir="$(cd "${script_dir}/.." && pwd)"
[[ -f "${release_dir}/VERSION" && -x "${release_dir}/WorkLens" ]] || {
  echo "Run install.sh from an extracted WorkLens macOS release package." >&2
  exit 1
}
version="$(tr -d '\r\n' < "${release_dir}/VERSION")"
version="${version#v}"
[[ "$version" =~ ^[0-9A-Za-z][0-9A-Za-z._-]*$ ]] || { echo "The release VERSION is invalid." >&2; exit 1; }

versions_dir="${install_dir}/versions"
version_dir="${versions_dir}/${version}"
current_file="${install_dir}/current.txt"
port_file="${install_dir}/port.txt"
installed_scripts="${install_dir}/scripts"
previous_version=""
previous_port=""
[[ -f "$current_file" ]] && previous_version="$(tr -d '\r\n' < "$current_file")"
[[ -f "$port_file" ]] && previous_port="$(tr -d '\r\n' < "$port_file")"
had_autostart=0
agent_plist="${HOME}/Library/LaunchAgents/com.worklens.app.plist"
if [[ -f "$agent_plist" ]] && grep -Fq "$install_dir" "$agent_plist"; then had_autostart=1; fi

mkdir -p "$versions_dir" "$bin_dir"
staging_dir="${versions_dir}/.staging-$$"
cleanup() {
  if [[ -d "$staging_dir" ]]; then
    rm -rf "$staging_dir"
  fi
}
trap cleanup EXIT

if [[ "$previous_version" != "$version" || ! -d "$version_dir" ]]; then
  mkdir -p "$staging_dir"
  cp -R "${release_dir}/." "$staging_dir/"
fi

if [[ -x "${installed_scripts}/manage.sh" ]]; then
  "${installed_scripts}/manage.sh" stop || true
fi

rollback() {
  local failed=$?
  set +e
  [[ -x "${installed_scripts}/manage.sh" ]] && "${installed_scripts}/manage.sh" stop
  if [[ -n "$previous_version" && -d "${versions_dir}/${previous_version}" ]]; then
    printf '%s\n' "$previous_version" > "$current_file"
    [[ -n "$previous_port" ]] && printf '%s\n' "$previous_port" > "$port_file"
    cp "${versions_dir}/${previous_version}/scripts/"*.sh "$installed_scripts/"
    chmod +x "$installed_scripts/"*.sh
    if [[ "$had_autostart" -eq 1 ]]; then "${installed_scripts}/manage.sh" register; else "${installed_scripts}/manage.sh" unregister; fi
    "${installed_scripts}/manage.sh" start
    echo "The new version failed to start. WorkLens was rolled back to ${previous_version}." >&2
  elif [[ -x "${installed_scripts}/manage.sh" ]]; then
    "${installed_scripts}/manage.sh" unregister
    rm -f "$current_file"
  fi
  if [[ "$version" != "$previous_version" && -d "$version_dir" ]]; then rm -rf "$version_dir"; fi
  exit "$failed"
}
trap rollback ERR

if [[ -d "$staging_dir" ]]; then
  rm -rf "$version_dir"
  mv "$staging_dir" "$version_dir"
fi
mkdir -p "$installed_scripts"
cp "${version_dir}/scripts/"*.sh "$installed_scripts/"
chmod +x "$installed_scripts/"*.sh "${version_dir}/WorkLens"
printf '%s\n' "$version" > "${current_file}.new"
mv "${current_file}.new" "$current_file"
printf '%s\n' "$port" > "$port_file"
printf '%s\n' "$bin_dir" > "${install_dir}/bin-dir.txt"

manager="${installed_scripts}/manage.sh"
printf '#!/usr/bin/env bash\nexec %q "$@"\n' "$manager" > "${bin_dir}/worklens"
chmod +x "${bin_dir}/worklens"

if [[ "$autostart" -eq 1 ]]; then "$manager" register; else "$manager" unregister; fi
"$manager" start
health_payload="$(curl -fsS --max-time 5 "http://127.0.0.1:${port}/healthz")"
printf '%s' "$health_payload" | grep -Fq "\"version\":\"${version}\"" || {
  echo "WorkLens started, but its health version did not match the installed version ${version}." >&2
  false
}
trap - ERR

if [[ "$open_browser" -eq 1 ]]; then open "http://127.0.0.1:${port}"; fi

for candidate in "$versions_dir"/*; do
  [[ -d "$candidate" ]] || continue
  name="$(basename "$candidate")"
  [[ "$name" == "$version" || "$name" == "$previous_version" ]] || rm -rf "$candidate"
done

echo "WorkLens ${version} is installed and running at http://127.0.0.1:${port}"
