#!/usr/bin/env bash
# Exercises offline accounts against a throwaway server. Run on the VPS; touches nothing live.
#
#   ./offlineprobe.sh
#
# Starts its own copy on port 3999 with its own state file, puts the offline join through the
# cases that matter, and prints a pass/fail line for each. The two ceilings are checked
# separately by varying X-Forwarded-For, which clientIP honours — behind Caddy that header is
# written by Caddy, and here it is what lets one machine pretend to be several.

set -u

PORT=3999
BASE="http://127.0.0.1:$PORT/v1"
STATE=/tmp/offlineprobe-state.json
pass=0; fail=0

ok() { echo "  PASS  $1"; pass=$((pass+1)); }
no() { echo "  FAIL  $1"; echo "        $2"; fail=$((fail+1)); }

check() { # name, expected, actual
  if [ "$2" = "$3" ]; then ok "$1"; else no "$1" "expected [$2] got [$3]"; fi
}

# join <ip> <hwid> <name> [uuid]
join() {
  local uuid="${4:-}"
  curl -s -X POST "$BASE/offline/join" -H "X-Forwarded-For: $1" \
    -H 'Content-Type: application/json' \
    -d "{\"name\":\"$3\",\"hwid\":\"$2\",\"uuid\":\"$uuid\"}"
}

field() { python3 -c "import sys,json; print(json.load(sys.stdin).get('$1',''))" 2>/dev/null; }

rm -f "$STATE"
ASOBU_STATE="$STATE" ASOBU_ADDR="127.0.0.1:$PORT" /tmp/asobu-check &
server=$!
trap 'kill $server 2>/dev/null' EXIT
sleep 1

echo "== joining =="

first=$(join 10.0.0.1 machine-aaaaaaaaaaaaaaaaaa Steve)
uuid=$(echo "$first" | field uuid)
tag=$(echo "$first" | field tag)
handle=$(echo "$first" | field handle)
token=$(echo "$first" | field token)

if [ -n "$uuid" ]; then ok "a join returns an account"; else no "a join returns an account" "$first"; fi
if [ ${#tag} -eq 4 ] && [ -z "${tag//[0-9]/}" ]; then ok "the tag is four digits ($tag)"; else no "the tag is four digits" "got [$tag]"; fi
check "the handle is name#tag" "Steve#$tag" "$handle"
if [ ${#token} -eq 64 ]; then ok "a token comes back"; else no "a token comes back" "got [$token]"; fi

echo "== coming back to the same account =="

again=$(join 10.0.0.1 machine-aaaaaaaaaaaaaaaaaa Steve "$uuid")
check "the same machine is handed its account back" "$uuid" "$(echo "$again" | field uuid)"
check "and keeps its tag" "$tag" "$(echo "$again" | field tag)"

stolen=$(join 10.0.0.9 machine-bbbbbbbbbbbbbbbbbb Steve "$uuid")
if [ "$(echo "$stolen" | field uuid)" != "$uuid" ]; then ok "another machine cannot claim it by uuid"; else no "another machine cannot claim it by uuid" "$stolen"; fi

echo "== five to a machine =="

rm -f "$STATE"; kill $server 2>/dev/null; sleep 0.3
ASOBU_STATE="$STATE" ASOBU_ADDR="127.0.0.1:$PORT" /tmp/asobu-check & server=$!; sleep 1

made=0
for i in 1 2 3 4 5 6; do
  # One machine, a different address every time, so only the machine ceiling can bite.
  out=$(join "10.1.0.$i" machine-cccccccccccccccccc "Player$i")
  [ -n "$(echo "$out" | field uuid)" ] && made=$((made+1))
  [ "$i" = 6 ] && sixth="$out"
done
check "five accounts to a machine" "5" "$made"
if echo "$sixth" | grep -q "computer"; then ok "the sixth is turned away, saying which ceiling"; else no "the sixth is turned away" "$sixth"; fi

echo "== five to an address =="

rm -f "$STATE"; kill $server 2>/dev/null; sleep 0.3
ASOBU_STATE="$STATE" ASOBU_ADDR="127.0.0.1:$PORT" /tmp/asobu-check & server=$!; sleep 1

made=0
for i in 1 2 3 4 5 6; do
  # One address, a different machine every time, so only the address ceiling can bite.
  out=$(join 10.2.0.7 "machine-dddddddddddddddd$i" "Guest$i")
  [ -n "$(echo "$out" | field uuid)" ] && made=$((made+1))
  [ "$i" = 6 ] && sixth="$out"
done
check "five accounts to an address" "5" "$made"
if echo "$sixth" | grep -q "connection"; then ok "the sixth is turned away, saying which ceiling"; else no "the sixth is turned away" "$sixth"; fi

echo "== finding people =="

rm -f "$STATE"; kill $server 2>/dev/null; sleep 0.3
ASOBU_STATE="$STATE" ASOBU_ADDR="127.0.0.1:$PORT" /tmp/asobu-check & server=$!; sleep 1

a=$(join 10.3.0.1 machine-eeeeeeeeeeeeeeeeee Alex)
b=$(join 10.3.0.2 machine-ffffffffffffffffff Notch)
atoken=$(echo "$a" | field token)
btag=$(echo "$b" | field tag)
buuid=$(echo "$b" | field uuid)

ask() {
  curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/friends/requests" \
    -H "Authorization: Bearer $atoken" -H 'Content-Type: application/json' \
    -d "{\"name\":\"$1\"}"
}

check "name#tag finds an offline account" "200" "$(ask "Notch#$btag")"
check "a bare name does not" "404" "$(ask "Notch")"
check "a wrong tag does not" "404" "$(ask "Notch#0000")"

echo "== what lands on disk =="

curl -s "$BASE/health" >/dev/null; sleep 1
kill $server 2>/dev/null; sleep 1

for secret in 10.3.0.1 10.3.0.2 machine-eeeeeeeeeeeeeeeeee machine-ffffffffffffffffff; do
  if grep -q "$secret" "$STATE" 2>/dev/null; then
    no "state.json does not hold [$secret]" "found it"
  else
    ok "state.json does not hold [$secret]"
  fi
done

if grep -q '"salt"' "$STATE" 2>/dev/null; then ok "a salt was made"; else no "a salt was made" "none in the file"; fi
if grep -q '"tag"' "$STATE" 2>/dev/null; then ok "tags are kept"; else no "tags are kept" "none in the file"; fi

# ---------------------------------------------------------------- sweeping the forgotten

echo "== forgetting accounts nobody has used =="

rm -f "$STATE"; kill $server 2>/dev/null; sleep 0.5

# Two offline accounts and one Mojang-style one, made friends with each other, then aged by hand.
# The sweep runs at startup, so writing the file and starting the server is the whole test.
python3 - "$STATE" <<'PY'
import json, sys, datetime

def when(days):
    return (datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(days=days)).isoformat()

state = {
  "users": {
    "stale001": {"uuid": "stale001", "name": "Ghost", "tag": "1111", "hwidKey": "keyA", "lastSeen": when(60)},
    "fresh002": {"uuid": "fresh002", "name": "Here",  "tag": "2222", "hwidKey": "keyB", "lastSeen": when(3)},
    "mojang03": {"uuid": "mojang03", "name": "Notch", "lastSeen": when(400)},
  },
  "friends": [
    {"from": "stale001", "to": "fresh002", "accepted": True, "since": when(60)},
    {"from": "mojang03", "to": "fresh002", "accepted": True, "since": when(60)},
  ],
  "tokens": {
    "hashA": {"uuid": "stale001", "expires": when(-30)},
    "hashB": {"uuid": "fresh002", "expires": when(-30)},
  },
  "shares": {},
  "salt": "0123456789abcdef",
  "offlineHwids": {"keyA": ["stale001"], "keyB": ["fresh002"]},
  "offlineIps": {"keyIP": ["stale001", "fresh002"]},
}
json.dump(state, open(sys.argv[1], "w"))
PY

ASOBU_STATE="$STATE" ASOBU_ADDR="127.0.0.1:$PORT" /tmp/asobu-check & server=$!; sleep 1.5

after=$(python3 -c "
import json
d = json.load(open('$STATE'))
users = d.get('users') or {}
print('stale' if 'stale001' in users else 'gone',
      'fresh' if 'fresh002' in users else 'lost',
      'mojang' if 'mojang03' in users else 'lost',
      len(d.get('friends') or []),
      len(d.get('tokens') or {}),
      len((d.get('offlineHwids') or {}).get('keyA', [])),
      len((d.get('offlineIps') or {}).get('keyIP', [])))
")
set -- $after

check "the unused offline account is forgotten" "gone" "$1"
check "the one in use is kept" "fresh" "$2"
check "a Mojang account is never swept, however long" "mojang" "$3"
check "its friendships go with it" "1" "$4"
check "and its sessions" "1" "$5"
check "its machine slot is freed" "0" "$6"
check "and its address slot" "1" "$7"

# ------------------------------------------------- one account, two machines, one friends list

echo "== the same account signed in twice =="

# The question this answers is about Mojang accounts on a second computer, but the property is
# not specific to them: a friends list belongs to an account, not to a session or a machine.
# Every route reads it from the authenticated uuid, and a second sign-in is simply a second
# token pointing at that same uuid. Shown here with an offline account because that is the one
# kind this probe can create without Mojang.

rm -f "$STATE"; kill $server 2>/dev/null; sleep 0.3
ASOBU_STATE="$STATE" ASOBU_ADDR="127.0.0.1:$PORT" /tmp/asobu-check & server=$!; sleep 1

one=$(join 10.4.0.1 machine-1111111111111111 Roamer)
oneuuid=$(echo "$one" | field uuid)
firstToken=$(echo "$one" | field token)

mate=$(join 10.4.0.2 machine-2222222222222222 Buddy)
matetag=$(echo "$mate" | field tag)

curl -s -o /dev/null -X POST "$BASE/friends/requests"   -H "Authorization: Bearer $firstToken" -H 'Content-Type: application/json'   -d "{\"name\":\"Buddy#$matetag\"}"

# Signing in again: a fresh session for the same account, as a second computer would get.
two=$(join 10.4.0.9 machine-1111111111111111 Roamer "$oneuuid")
secondToken=$(echo "$two" | field token)

if [ "$firstToken" != "$secondToken" ]; then ok "signing in again is a different session"; else no "signing in again is a different session" "same token twice"; fi

listOf() {
  curl -s "$BASE/friends" -H "Authorization: Bearer $1" |
    python3 -c "import sys,json; d=json.load(sys.stdin); print(len(d.get('friends') or []), len(d.get('outgoing') or []))"
}

check "the first session sees the request it sent" "0 1" "$(listOf "$firstToken")"
check "and so does the new one, with no setup" "0 1" "$(listOf "$secondToken")"
check "the older session still works alongside it" "0 1" "$(listOf "$firstToken")"

echo
echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
