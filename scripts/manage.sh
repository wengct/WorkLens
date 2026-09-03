#!/usr/bin/env bash
set -euo pipefail

command="${1:-open}"
[[ $# -gt 0 ]] && shift
install_dir="$(cd "$(dirname "$0")/.." && pwd)"
label="com.worklens.app"
plist="${HOME}/Library/LaunchAgents/${label}.plist"
run_script="${install_dir}/scripts/run.sh"
port_file="${install_dir}/port.txt"
pid_file="${install_dir}/app.pid"

port() { tr -d '\r\n' < "$port_file"; }
url() { printf 'http://127.0.0.1:%s' "$(port)"; }
healthy() { curl -fsS --max-time 2 "$(url)/healthz" 2>/dev/null | grep -q '"status":"Healthy"'; }
wait_healthy() {
  local count=0
  while [[ $count -lt 60 ]]; do
    healthy && return 0
    sleep 0.5
    count=$((count + 1))
  done
  echo "WorkLens did not become healthy within 30 seconds. Check the WorkLens log directory for details." >&2
  return 1
}
xml_escape() { printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' -e 's/"/\&quot;/g' -e "s/'/\&apos;/g"; }
loaded() { launchctl print "gui/${UID}/${label}" >/dev/null 2>&1; }
owns_agent() { [[ -f "$plist" ]] && grep -Fq "$(xml_escape "$run_script")" "$plist"; }

register_agent() {
  local log_dir run_xml out_xml err_xml
  log_dir="${HOME}/Library/Application Support/WorkLens/logs"
  mkdir -p "$(dirname "$plist")" "$log_dir"
  if [[ -f "$plist" ]] && ! owns_agent; then
    echo "A LaunchAgent named ${label} already exists and belongs to another installation." >&2
    return 1
  fi
  run_xml="$(xml_escape "$run_script")"
  out_xml="$(xml_escape "${log_dir}/launchd-stdout.log")"
  err_xml="$(xml_escape "${log_dir}/launchd-stderr.log")"
  loaded && launchctl bootout "gui/${UID}/${label}" >/dev/null 2>&1 || true
  printf '%s\n' \
    '<?xml version="1.0" encoding="UTF-8"?>' \
    '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">' \
    '<plist version="1.0"><dict>' \
    '<key>Label</key><string>com.worklens.app</string>' \
    "<key>ProgramArguments</key><array><string>/bin/bash</string><string>${run_xml}</string><string>$(xml_escape "$install_dir")</string></array>" \
    '<key>RunAtLoad</key><true/>' \
    '<key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>' \
    '<key>ProcessType</key><string>Background</string>' \
    '<key>ThrottleInterval</key><integer>10</integer>' \
    "<key>StandardOutPath</key><string>${out_xml}</string>" \
    "<key>StandardErrorPath</key><string>${err_xml}</string>" \
    '</dict></plist>' > "$plist"
  plutil -lint "$plist" >/dev/null
  launchctl bootstrap "gui/${UID}" "$plist"
}

unregister_agent() {
  if owns_agent; then
    loaded && launchctl bootout "gui/${UID}/${label}" >/dev/null 2>&1 || true
    rm -f "$plist"
  fi
}

stop_app() {
  if owns_agent; then
    loaded && launchctl bootout "gui/${UID}/${label}" >/dev/null 2>&1 || true
  fi
  if [[ -f "$pid_file" ]]; then
    app_pid="$(tr -d '\r\n' < "$pid_file")"
    [[ "$app_pid" =~ ^[0-9]+$ ]] && kill "$app_pid" >/dev/null 2>&1 || true
    rm -f "$pid_file"
  fi
  local count=0
  while healthy && [[ $count -lt 40 ]]; do sleep 0.25; count=$((count + 1)); done
}

start_app() {
  healthy && return 0
  if owns_agent; then
    loaded || launchctl bootstrap "gui/${UID}" "$plist"
    launchctl kickstart -k "gui/${UID}/${label}"
  else
    nohup /bin/bash "$run_script" "$install_dir" >/dev/null 2>&1 &
  fi
  wait_healthy
}

case "$command" in
  register) register_agent ;;
  unregister) unregister_agent ;;
  start) start_app; echo "WorkLens is running at $(url)" ;;
  stop) stop_app; echo "WorkLens is stopped." ;;
  restart) stop_app; start_app; echo "WorkLens restarted." ;;
  status)
    if healthy; then echo "WorkLens is healthy at $(url)"; else echo "WorkLens is not running or is unhealthy."; exit 1; fi
    ;;
  open) start_app; open "$(url)" ;;
  uninstall)
    purge=""
    [[ "${1:-}" == "--purge-data" ]] && purge="--purge-data"
    "${install_dir}/scripts/uninstall.sh" ${purge:+$purge}
    ;;
  *) echo "Usage: worklens [open|start|stop|restart|status|uninstall [--purge-data]]" >&2; exit 2 ;;
esac
