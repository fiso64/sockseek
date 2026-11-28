#!/usr/bin/env bash
# Run as root on a disposable systemd host with curl and jq installed.
set -euo pipefail
deb=$(realpath "${1:?Pass the release .deb}")
conf=/etc/sockseek/sockseek.conf
state=/var/lib/sockseek
[[ $EUID == 0 && ! -e /etc/sockseek && ! -e $state ]]
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
# Install identical payloads under successive versions to exercise dpkg's
# real upgrade path without rebuilding the runtime or needing a prior release.
dpkg-deb --raw-extract "$deb" "$work/older"
sed -i '/^Version:/s/$/~installtest/' "$work/older/DEBIAN/control"
dpkg-deb -Zgzip -z1 --build "$work/older" "$work/older.deb"
export DEBIAN_FRONTEND=noninteractive
apt-get -o DPkg::Lock::Timeout=90 install -y "$work/older.deb"
[[ $(systemctl is-enabled sockseek || :) == disabled ]]
[[ $(systemctl is-active sockseek || :) == inactive ]]
[[ $(stat -c '%U:%G:%a' "$conf") == root:sockseek:640 ]]
/usr/bin/sockseek --version

fixture=/run/sockseek-deb-test
install -d -m755 "$fixture"
echo 'download fixture' > "$fixture/Fixture Artist - Fixture Song.mp3"
cat >>"$conf" <<CONFIG
mock-files-dir = $fixture
mock-files-no-read-tags = true
web-password-file = $fixture/web-password
CONFIG
install -o root -g sockseek -m640 /dev/null "$fixture/web-password"
echo browser-test > "$fixture/web-password"

cp "$conf" "$work/expected.conf"

url=http://127.0.0.1:5030
login() {
    curl -fsS --retry 60 --retry-connrefused --retry-delay 1 "$url/" -o "$work/index.html"
    grep -q data-docs "$work/index.html"
    local token
    token=$(curl -fsS -c "$work/cookies" "$url/api/auth/session" | jq -er .antiforgeryToken)
    curl -fsS -b "$work/cookies" -c "$work/cookies" -H "X-CSRF-Token: $token" --json '{"username":"admin","password":"browser-test"}' "$url/api/auth/login" >/dev/null
}
get() { curl -fsS -b "$work/cookies" -c "$work/cookies" "$url$1"; }

systemctl enable --now sockseek
login
# Another local user, such as a media server, reads downloads without extra setup.
useradd --system --no-create-home sockseek-media-test
csrf=$(get /api/auth/session | jq -er .antiforgeryToken)
job=$(curl -fsS -b "$work/cookies" -H "X-CSRF-Token: $csrf" \
    --json '{"songQuery":{"artist":"Fixture Artist","title":"Fixture Song"}}' \
    "$url/api/jobs/downloads/song" | jq -er .jobId)
for ((i=0; i<45; i++)); do
    if get "/api/jobs/$job" | jq -e '.summary.lifecycle.terminalOutcome == "Succeeded"' >/dev/null; then break; fi
    sleep 1
done
get "/api/jobs/$job" | jq -e '.summary.lifecycle.terminalOutcome == "Succeeded"' >/dev/null
transfer=$(get '/api/transfers?direction=download' | jq -er '.items[0].transferId')
test -d "$state/downloads"
test -s "$state/data/sockseek.db"
[[ $(stat -c %a "$state/data") == 700 && $(stat -c %a "$state") == 755 ]]
media=$(find "$state/downloads" -name '*.mp3' -type f -print -quit)
runuser -u sockseek-media-test -- cmp "$media" "$fixture/Fixture Artist - Fixture Song.mp3"
invocation=$(systemctl show -p InvocationID --value sockseek)
apt-get -o DPkg::Lock::Timeout=90 install -y "$deb"
systemctl is-active --quiet sockseek
[[ $(systemctl show -p InvocationID --value sockseek) != "$invocation" ]]
systemctl is-enabled --quiet sockseek
cmp "$conf" "$work/expected.conf"
login
get "/api/jobs/$job" | jq -e '.summary.lifecycle.terminalOutcome == "Succeeded"' >/dev/null
get '/api/transfers?direction=download' | jq -e --arg id "$transfer" '.items[0].transferId == $id' >/dev/null
runuser -u sockseek-media-test -- cmp "$media" "$fixture/Fixture Artist - Fixture Song.mp3"
