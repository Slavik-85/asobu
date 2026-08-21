#!/bin/bash
# Chat: does it reach the other person, and does any of it touch the disk?
set -u

DIR=$(mktemp -d)
STATE="$DIR/state.json"
PORT=3111
U="localhost:$PORT"
A="Authorization: Bearer alice-token"
B="Authorization: Bearer bob-token"
C="Authorization: Bearer carol-token"

hash() { printf '%s' "$1" | sha256sum | cut -d' ' -f1; }

AH=$(hash alice-token); BH=$(hash bob-token); CH=$(hash carol-token)
SOON=$(date -u -d '+80 days' +%Y-%m-%dT%H:%M:%SZ)
NOW=$(date -u +%Y-%m-%dT%H:%M:%SZ)

# Alice and Bob are friends. Carol is a stranger who has asked Alice and not been answered.
cat > "$STATE" <<JSON
{
  "users": {
    "aaaa0000000000000000000000000001": {"uuid":"aaaa0000000000000000000000000001","name":"Alice","lastSeen":"$NOW"},
    "bbbb0000000000000000000000000002": {"uuid":"bbbb0000000000000000000000000002","name":"Bob","lastSeen":"$NOW"},
    "cccc0000000000000000000000000003": {"uuid":"cccc0000000000000000000000000003","name":"Carol","lastSeen":"$NOW"}
  },
  "friends": [
    {"from":"aaaa0000000000000000000000000001","to":"bbbb0000000000000000000000000002","accepted":true,"since":"$NOW"},
    {"from":"cccc0000000000000000000000000003","to":"aaaa0000000000000000000000000001","accepted":false,"since":"$NOW"}
  ],
  "tokens": {
    "$AH": {"uuid":"aaaa0000000000000000000000000001","expires":"$SOON"},
    "$BH": {"uuid":"bbbb0000000000000000000000000002","expires":"$SOON"},
    "$CH": {"uuid":"cccc0000000000000000000000000003","expires":"$SOON"}
  },
  "shares": {}
}
JSON

ASOBU_STATE="$STATE" ASOBU_ADDR=":$PORT" /tmp/asobu-api-check >"$DIR/log" 2>&1 &
SERVER=$!
trap 'kill $SERVER 2>/dev/null; rm -rf "$DIR"' EXIT
sleep 1

if ! curl -s --max-time 5 "$U/v1/health" | grep -q ok; then
  echo "server did not start:"; cat "$DIR/log"; exit 1
fi

say() { curl -s -o /dev/null -w '%{http_code}' --max-time 10 -X POST -H "$1" \
        -H 'Content-Type: application/json' "$U/v1/chat" -d "$2"; }
inbox() { curl -s --max-time 10 -H "$1" "$U/v1/friends" | python3 -c \
        "import json,sys; d=json.load(sys.stdin); print(json.dumps(d.get('messages',[])))"; }

echo "== a message reaches the other person =="
say "$A" '{"to":"bbbb0000000000000000000000000002","text":"hey, launching in 5"}' >/dev/null
got=$(inbox "$B")
echo "   Bob sees: $got"
printf '%s' "$got" | grep -q "launching in 5" && echo "   PASS" || { echo "   FAIL"; exit 1; }

echo
echo "== and is gone once collected =="
again=$(inbox "$B")
echo "   Bob asks again: $again"
[ "$again" = "[]" ] && echo "   PASS — handed over once, then forgotten" || { echo "   FAIL — still held"; exit 1; }

echo
echo "== the sender is not given their own message back =="
say "$A" '{"to":"bbbb0000000000000000000000000002","text":"second"}' >/dev/null
mine=$(inbox "$A")
[ "$mine" = "[]" ] && echo "   PASS" || { echo "   FAIL: $mine"; exit 1; }
inbox "$B" >/dev/null

echo
echo "== nothing reaches the disk =="
say "$A" '{"to":"bbbb0000000000000000000000000002","text":"SECRETPHRASE-do-not-persist"}' >/dev/null
sleep 32   # past one whole flush, which is when state.json is written
if grep -q "SECRETPHRASE" "$STATE"; then
  echo "   FAIL — the message is in state.json"; exit 1
fi
echo "   PASS — state.json has no trace of it ($(stat -c%s "$STATE") bytes)"
grep -q "SECRETPHRASE" "$DIR"/* 2>/dev/null && echo "   NOTE: found in another file" || echo "   PASS — nor anywhere else in the data directory"

echo
echo "== it survived in memory across that flush =="
still=$(inbox "$B")
printf '%s' "$still" | grep -q "SECRETPHRASE" && echo "   PASS — still deliverable, just never written down" || echo "   NOTE: expired or lost"

echo
echo "== a stranger cannot message =="
printf "   Carol -> Alice (request pending, not accepted): "
say "$C" '{"to":"aaaa0000000000000000000000000001","text":"buy my thing"}'
echo " (expect 403)"

echo
echo "== an empty message is refused =="
printf "   "; say "$A" '{"to":"bbbb0000000000000000000000000002","text":"   "}'; echo " (expect 400)"

echo
echo "== an over-long message is refused =="
long=$(python3 -c "print('x'*2500)")
printf "   "; say "$A" "$(python3 -c "
import json;print(json.dumps({'to':'bbbb0000000000000000000000000002','text':'$long'}))")"; echo " (expect 400)"

echo
echo "== without a session =="
printf "   "; curl -s -o /dev/null -w '%{http_code}' --max-time 10 -X POST \
  -H 'Content-Type: application/json' "$U/v1/chat" \
  -d '{"to":"bbbb0000000000000000000000000002","text":"hi"}'; echo " (expect 401)"

echo
echo "== the watch delivers it without waiting for the timeout =="
rev=$(curl -s --max-time 10 -H "$B" "$U/v1/friends" | python3 -c "import json,sys;print(json.load(sys.stdin)['revision'])")
( start=$(date +%s%3N)
  body=$(curl -s --max-time 40 -H "$B" "$U/v1/friends/watch?since=$rev")
  ms=$(( $(date +%s%3N) - start ))
  n=$(printf '%s' "$body" | python3 -c "import json,sys;print(len(json.load(sys.stdin).get('messages',[])))")
  echo "   Bob's watch answered after ${ms}ms with $n message(s)" ) &
watcher=$!
sleep 2
say "$A" '{"to":"bbbb0000000000000000000000000002","text":"over here"}' >/dev/null
echo "   Alice sent at t=2s"
wait $watcher

echo
echo "== state.json still holds only friends =="
python3 -c "
import json
d = json.load(open('$STATE'))
print('   keys:', sorted(d))
print('   users:', len(d['users']), ' friends:', len(d['friends']), ' shares:', len(d['shares']))
assert 'inbox' not in d and 'messages' not in d, 'a message store reached the file'
print('   PASS - no message store in the file')
"
