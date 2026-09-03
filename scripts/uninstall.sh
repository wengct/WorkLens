#!/usr/bin/env bash
set -euo pipefail

install_dir="$(cd "$(dirname "$0")/.." && pwd)"
bin_dir="${HOME}/.local/bin"
purge_data=0
[[ "${1:-}" == "--purge-data" ]] && purge_data=1
[[ -f "${install_dir}/bin-dir.txt" ]] && bin_dir="$(tr -d '\r\n' < "${install_dir}/bin-dir.txt")"

if [[ -z "$install_dir" || "$install_dir" == "/" || "$install_dir" == "$HOME" || ${#install_dir} -lt 8 ]]; then
  echo "Refusing to uninstall from an unsafe installation path: ${install_dir}" >&2
  exit 1
fi

"${install_dir}/scripts/manage.sh" stop || true
"${install_dir}/scripts/manage.sh" unregister || true
rm -f "${bin_dir}/worklens"
rm -rf "$install_dir"

if [[ "$purge_data" -eq 1 ]]; then
  data_dir="${HOME}/Library/Application Support/WorkLens"
  if [[ -d "$data_dir" ]]; then
    printf "Type DELETE to permanently remove all WorkLens data from '%s': " "$data_dir"
    read -r confirmation
    if [[ "$confirmation" == "DELETE" ]]; then
      rm -rf "$data_dir"
      echo "WorkLens was uninstalled and its user data was removed."
      exit 0
    fi
  fi
fi

echo "WorkLens was uninstalled. Its user data was preserved."
