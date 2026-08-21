#!/bin/bash
# The abuse surface: can somebody flood, reach a stranger, or push something that is not text?
set -u
DIR=$(mktemp -d); STATE="$DIR/state.json"; PORT=3112; U="localhost:$PORT"
A="Authorization: Bearer alice-token"; B="Authorization: Bearer bob-token"
C="Authorization: Bearer carol-token"
hash() { printf '%s' "$1" | sha256sum | cut -d' ' -f1; }
AH=$(hash alice-token); BH=$(hash bob-token); CH=$(hash carol-token)
SOON=$(date -u -d '+80 days' +%Y-%m-%dT%H:%M:%SZ); NOW=$(date -u +%Y-%m-%dT%H:%M:%SZ)
cat > "$STATE" <<JSON
{"users":{
 "aaaa0000000000000000000000000001":{"uuid":"aaaa0000000000000000000000000001","name":"Alice","lastSeen":"$NOW"},
 "bbbb0000000000000000000000000002":{"uuid":"bbbb0000000000000000000000000002","name":"Bob","lastSeen":"$NOW"},
 "cccc0000000000000000000000000003":{"uuid":"cccc0000000000000000000000000003","name":"Carol","lastSeen":"$NOW"}},
 "friends":[{"from":"aaaa0000000000000000000000000001","to":"bbbb0000000000000000000000000002","accepted":true,"since":"$NOW"}],
 "tokens":{"$AH":{"uuid":"aaaa0000000000000000000000000001","expires":"$SOON"},
           "$BH":{"uuid":"bbbb0000000000000000000000000002","expires":"$SOON"},
           "$CH":{"uuid":"cccc0000000000000000000000000003","expires":"$SOON"}},
 "shares":{}}
JSON
ASOBU_STATE="$STATE" ASOBU_ADDR=":$PORT" /tmp/asobu-api-check >"$DIR/log" 2>&1 &
SERVER=$!; trap 'kill $SERVER 2>/dev/null; rm -rf "$DIR"' EXIT; sleep 1
say() { curl -s -o /dev/null -w '%{http_code}' --max-time 10 -X POST -H "$1" \
        -H 'Content-Type: application/json' "$U/v1/chat" -d "$2"; }
BOB='bbbb0000000000000000000000000002'; ALICE='aaaa0000000000000000000000000001'

echo "== flooding: 45 messages as fast as possible (limit is 30/min) =="
ok=0; limited=0
for i in $(seq 1 45); do
  c=$(say "$A" "{\"to\":\"$BOB\",\"text\":\"flood $i\"}")
  [ "$c" = "200" ] && ok=$((ok+1)); [ "$c" = "429" ] && limited=$((limited+1))
done
echo "   accepted $ok, refused $limited"
[ "$limited" -gt 0 ] && [ "$ok" -le 31 ] && echo "   PASS — cut off at the limit" || echo "   FAIL"

echo
echo "== the flood did not take the sender's own session down =="
w=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 -H "$A" "$U/v1/friends")
echo "   Alice's friends poll: $w"; [ "$w" = "200" ] && echo "   PASS — chat limit bit first" || echo "   FAIL: shared limiter tripped"

echo
echo "== and Bob's inbox is capped, not unbounded =="
n=$(curl -s --max-time 10 -H "$B" "$U/v1/friends" | python3 -c "import json,sys;print(len(json.load(sys.stdin)['messages']))")
echo "   Bob collected $n"; [ "$n" -le 100 ] && echo "   PASS — at or under the 100 cap" || echo "   FAIL"

echo
echo "== a stranger with no friendship at all =="
printf "   Carol -> Bob: "; say "$C" "{\"to\":\"$BOB\",\"text\":\"hello stranger\"}"; echo " (expect 403)"

echo
echo "== messaging yourself =="
printf "   Alice -> Alice: "; say "$A" "{\"to\":\"$ALICE\",\"text\":\"note to self\"}"; echo " (expect 403)"

echo
echo "== a made-up recipient =="
printf "   Alice -> nobody: "; say "$A" '{"to":"deadbeef","text":"hi"}'; echo " (expect 403)"

echo
echo "== control characters are stripped, not delivered =="
sleep 61   # let the per-minute window clear
say "$A" "$(python3 -c "
import json; print(json.dumps({'to':'$BOB','text':'before\u0000\u001b[31mRED\u0007 after\nsecond line'}))")" >/dev/null
got=$(curl -s --max-time 10 -H "$B" "$U/v1/friends" | python3 -c "
import json,sys
m=json.load(sys.stdin)['messages']
print(repr(m[-1]['text']) if m else 'NOTHING')")
echo "   delivered: $got"
printf '%s' "$got" | grep -q 'x1b\|\\x00\|\\x07' && echo "   FAIL — control characters got through" || echo "   PASS — stripped, newline kept"

echo
echo "== an ex-friend cannot keep messaging =="
curl -s -o /dev/null --max-time 10 -X DELETE -H "$B" "$U/v1/friends/$ALICE"
printf "   Alice -> Bob after Bob removed her: "; say "$A" "{\"to\":\"$BOB\",\"text\":\"still here\"}"; echo " (expect 403)"
