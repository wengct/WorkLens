#!/usr/bin/env bash
set -euo pipefail

release_dir="${1:?Usage: test-install.sh RELEASE_DIR}"
test_root="$(mktemp -d "${TMPDIR:-/tmp}/worklens-install-test.XXXXXX")"
install_dir="${test_root}/Install Path 中文"
bin_dir="${test_root}/bin path"
runtime_dir="${test_root}/runtime data"
port=$((18000 + RANDOM % 10000))
mkdir -p "$runtime_dir"

export ConnectionStrings__WorkLens="Data Source=${runtime_dir}/worklens.db"
export WorkLens__BackupPath="${runtime_dir}/backups"
export WorkLens__LogPath="${runtime_dir}/logs"

cleanup() {
  if [[ -x "${install_dir}/scripts/manage.sh" ]]; then "${install_dir}/scripts/manage.sh" stop || true; fi
  rm -rf "$test_root"
}
trap cleanup EXIT

"${release_dir}/scripts/install.sh" --install-dir "$install_dir" --bin-dir "$bin_dir" --port "$port" --no-autostart --no-open-browser
curl -fsS --max-time 5 "http://127.0.0.1:${port}/healthz" | grep -q '"status":"Healthy"'
curl -fsS --max-time 5 "http://127.0.0.1:${port}/app.css" >/dev/null
[[ -f "${runtime_dir}/worklens.db" ]]

# Reinstalling the same version must preserve user data.
"${release_dir}/scripts/install.sh" --install-dir "$install_dir" --bin-dir "$bin_dir" --port "$port" --no-autostart --no-open-browser
[[ -f "${runtime_dir}/worklens.db" ]]

# A package whose declared version does not match the running assembly must roll back.
bad_release="${test_root}/bad release"
cp -R "$release_dir" "$bad_release"
printf '%s\n' '0.0.0-broken' > "${bad_release}/VERSION"
if "${bad_release}/scripts/install.sh" --install-dir "$install_dir" --bin-dir "$bin_dir" --port "$port" --no-autostart --no-open-browser; then
  echo "A mismatched package version unexpectedly installed successfully." >&2
  exit 1
fi
expected_version="$(tr -d '\r\n' < "${release_dir}/VERSION")"
expected_version="${expected_version#v}"
[[ "$(tr -d '\r\n' < "${install_dir}/current.txt")" == "$expected_version" ]]
curl -fsS --max-time 5 "http://127.0.0.1:${port}/healthz" | grep -Fq "\"version\":\"${expected_version}\""

"${install_dir}/scripts/manage.sh" uninstall
[[ -f "${runtime_dir}/worklens.db" ]]
echo "macOS installation smoke test passed."
