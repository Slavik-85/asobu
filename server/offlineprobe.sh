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

echo
echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
