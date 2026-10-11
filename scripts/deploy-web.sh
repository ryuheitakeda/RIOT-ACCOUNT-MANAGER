#!/usr/bin/env bash
# Builds the web app and runs it under launchd on this Mac, from server/ in this repository (gitignored).
#   scripts/deploy-web.sh             test, publish, swap in the new build (the old one stays in server/app.prev), restart
#   scripts/deploy-web.sh --rollback  swap server/app.prev back in and restart
# Settings come from server/deploy.env (see deploy/macos/deploy.env.example). The data in server/data is never touched.
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
server="$repo/server"
env_file="$server/deploy.env"
if [[ ! -f "$env_file" ]]; then
  mkdir -p "$server"
  cp "$repo/deploy/macos/deploy.env.example" "$env_file"
  echo "Created $env_file. Fill in PUBLIC_HOST and run again." >&2
  exit 1
fi
# shellcheck disable=SC1090
source "$env_file"
: "${LAUNCHD_LABEL:?}" "${WEB_PORT:?}" "${SERVE_PORT:?}" "${PUBLIC_HOST:?set PUBLIC_HOST in server/deploy.env}"

plist="$HOME/Library/LaunchAgents/$LAUNCHD_LABEL.plist"
logs="$HOME/Library/Logs/RiotAccountsWeb"
domain="gui/$(id -u)"

stop() { launchctl bootout "$domain/$LAUNCHD_LABEL" 2>/dev/null || true; }

start() {
  mkdir -p "$logs" "$server/data"
  chmod 700 "$server/data"
  sed -e "s|{{LABEL}}|$LAUNCHD_LABEL|g" -e "s|{{APP}}|$server/app|g" -e "s|{{DATA}}|$server/data|g" \
      -e "s|{{PORT}}|$WEB_PORT|g" -e "s|{{HOSTS}}|localhost;127.0.0.1;$PUBLIC_HOST|g" -e "s|{{LOGS}}|$logs|g" \
      "$repo/deploy/macos/riot-accounts-web.plist.template" > "$plist"
  plutil -lint "$plist" >/dev/null
  launchctl bootstrap "$domain" "$plist"
  for _ in $(seq 1 30); do
    if curl -fsS "http://127.0.0.1:$WEB_PORT/api/status" >/dev/null 2>&1; then
      echo "Running: http://127.0.0.1:$WEB_PORT/ ($(curl -fsS "http://127.0.0.1:$WEB_PORT/api/status" | sed -E 's/.*"version":"([^"]*)".*/\1/'))"
      if ! tailscale serve status 2>/dev/null | grep -q ":$SERVE_PORT "; then
        echo "Not published on the tailnet yet. Run once: tailscale serve --bg --https=$SERVE_PORT http://127.0.0.1:$WEB_PORT"
      else
        echo "Tailnet: https://$PUBLIC_HOST:$SERVE_PORT/"
      fi
      return 0
    fi
    sleep 1
  done
  echo "The server did not answer. See $logs/server-error.log" >&2
  return 1
}

if [[ "${1:-}" == "--rollback" ]]; then
  [[ -d "$server/app.prev" ]] || { echo "No server/app.prev to roll back to." >&2; exit 1; }
  stop
  rm -rf "$server/app.failed"
  mv "$server/app" "$server/app.failed"
  mv "$server/app.prev" "$server/app"
  start
  exit
fi

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet-riot}"
export PATH="$DOTNET_ROOT:$PATH"
dotnet test "$repo/tests/RiotAccounts.Core.Tests" -c Release --nologo -v q
rm -rf "$server/app.new"
dotnet publish "$repo/src/RiotAccounts.Web" -c Release -r osx-arm64 --self-contained true \
  -p:DebugType=None -p:DebugSymbols=false --nologo -v q -o "$server/app.new"

stop
rm -rf "$server/app.prev"
[[ -d "$server/app" ]] && mv "$server/app" "$server/app.prev"
mv "$server/app.new" "$server/app"
start
