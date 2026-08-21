package main

import (
	"encoding/base64"
	"net/http"
	"time"
)

// One message on its way from one friend to another.
//
// Deliberately not part of State. State is what saveNow marshals to state.json, and the one
// thing chat is not allowed to do is put messages on a disk — so they live on the Server itself,
// beside the auth handshakes, where nothing can serialise them by accident. A restart forgets
// every message in flight, which is the correct amount of chat history for a relay to keep.
type message struct {
	From string `json:"from"`
	Name string `json:"name"`

	// Nonce, ciphertext and authentication tag, base64. Sealed by the sender against the
	// recipient's published key, so this process relays bytes it has no way to read — and the
	// tag means it cannot alter them either without the recipient noticing.
	Box string    `json:"box"`
	At  time.Time `json:"at"`
}

const (
	// The sealed box, base64. Sized for a picture rather than a sentence: the sending launcher
	// shrinks and re-encodes every image to at most 400 KB, which is about 550 KB once base64
	// has had it. The server cannot tell a picture from a paragraph — that is the point — so one
	// ceiling has to cover both.
	maxBoxBytes = 700_000

	// Messages one person may have waiting. Kept alongside the byte ceiling below rather than
	// replaced by it: a hundred is the number that stops somebody being buried in text, and
	// bytes are the number that stops them being buried in pictures.
	maxWaiting = 100

	// And what those may weigh in total. This is the ceiling that actually binds once images
	// exist — a hundred messages meant a few hundred kilobytes when they were all sentences and
	// would mean fifty megabytes if they were all photographs. About ten pictures, or every
	// sentence anybody will type in ten minutes.
	maxWaitingBytes = 6 << 20

	// How long an uncollected message waits before it is forgotten. Chat here is for two people
	// who are both around; one that nobody came for in this long is better re-sent than
	// delivered out of nowhere hours later.
	messageLife = 10 * time.Minute

	// Per sender. One every two seconds is faster than anybody types for long, and well under
	// the 240-per-five-minutes every authenticated call already shares — deliberately, so a
	// sender who runs away hits this and not that. Tripping the shared limiter would take their
	// watch down with them, which turns one person's flood into their own outage.
	messagesPerMinute = 30

	// Held for everyone, everywhere. The ceilings above bound one conversation; this bounds the
	// process, and is the one that matters on a machine with 3 GB free. Reached only by a great
	// many people all offline at once with pictures waiting.
	maxHeldBytes = 192 << 20
)

// handleChatSend takes one message and leaves it where its recipient will next look.
//
// This is the whole of chat as far as the server is concerned. There is no conversation, no
// history, no read state and no delivery receipt, because every one of those would mean keeping
// the messages.
func (s *Server) handleChatSend(w http.ResponseWriter, r *http.Request) {
	me := s.authed(w, r)
	if me == nil {
		return
	}

	if !s.limiter.allow("chat:"+me.UUID, messagesPerMinute, time.Minute) {
		fail(w, http.StatusTooManyRequests, "slow down")
		return
	}

	var body struct {
		To  string `json:"to"`
		Box string `json:"box"`
	}

	// The one route with a raised ceiling, and only a little above what a box may be — enough
	// for the JSON around it and not enough to be somewhere to push data.
	if !readBodyUpTo(w, r, &body, maxBoxBytes+4096) {
		return
	}

	// All that can be checked about a message nobody here can read: that there is one, and that
	// it is not enormous. Emptiness, length in characters and anything about the text itself are
	// the sending client's job now — necessarily, since that is the last place it exists in the
	// clear.
	if body.Box == "" {
		fail(w, http.StatusBadRequest, "nothing to send")
		return
	}
	if len(body.Box) > maxBoxBytes {
		fail(w, http.StatusBadRequest, "that message is too long")
		return
	}

	// Accepted friends only. A request that has been sent and not answered is not a
	// conversation — without this, asking to be somebody's friend would be enough to start
	// talking at them, which is the shape every unsolicited-message problem has.
	if friendship, _ := s.between(me.UUID, body.To); friendship == nil || !friendship.Accepted {
		fail(w, http.StatusForbidden, "you can only message friends")
		return
	}

	if s.heldBytes()+len(body.Box) > maxHeldBytes {
		fail(w, http.StatusServiceUnavailable, "too much in flight, try again shortly")
		return
	}

	// Room is made rather than refused: the recipient is behind, and the newest thing said to
	// them is worth more than the oldest. Both ceilings drop from the front until the new one
	// fits under each.
	waiting := s.inbox[body.To]

	for len(waiting) >= maxWaiting {
		waiting = waiting[1:]
	}
	for bytesOf(waiting)+len(body.Box) > maxWaitingBytes && len(waiting) > 0 {
		waiting = waiting[1:]
	}

	s.inbox[body.To] = append(waiting, message{
		From: me.UUID,
		Name: me.Name,
		Box:  body.Box,
		At:   time.Now(),
	})

	// Wakes every open watch, the recipient's among them. That is the whole delivery mechanism:
	// their launcher is already holding a request open, and this is what makes it answer.
	s.bump()

	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

// bytesOf is what one person's waiting messages weigh.
func bytesOf(waiting []message) int {
	total := 0
	for _, m := range waiting {
		total += len(m.Box)
	}

	return total
}

// heldBytes is what every message waiting anywhere weighs, for the ceiling on the process.
func (s *Server) heldBytes() int {
	total := 0
	for _, waiting := range s.inbox {
		total += bytesOf(waiting)
	}

	return total
}

// takeMessages hands over everything waiting for one person and immediately forgets it.
//
// Handing over and forgetting are one step on purpose. A message that has been delivered is a
// message the server has no further business holding, and the surest way not to accumulate
// something is never to have a moment where keeping it was an option.
//
// The cost of that is a message delivered to a launcher that then crashes before showing it is
// gone. Worth it: the alternative is a server that remembers conversations.
func (s *Server) takeMessages(uuid string) []message {
	waiting := s.inbox[uuid]
	if len(waiting) == 0 {
		return []message{}
	}

	delete(s.inbox, uuid)

	return waiting
}

// dropStaleMessages forgets whatever nobody came to collect, so a friend who never opens Asobu
// again cannot hold memory forever on the strength of things said to them once.
func (s *Server) dropStaleMessages(now time.Time) {
	for uuid, waiting := range s.inbox {
		kept := waiting[:0]

		for _, m := range waiting {
			if now.Sub(m.At) < messageLife {
				kept = append(kept, m)
			}
		}

		if len(kept) == 0 {
			delete(s.inbox, uuid)
			continue
		}

		s.inbox[uuid] = kept
	}
}

// handlePublishKey records the public half of a launcher's chat key.
//
// Public keys are the one part of chat that is stored, because a public key is not a secret and
// friends have to be able to fetch one to send anything. What it costs is the honest caveat on
// the whole feature: this server hands out the keys, so a server that lied could hand out its
// own and read everything. The fingerprint each conversation shows is the answer to that, and it
// only works if the two people actually compare it.
func (s *Server) handlePublishKey(w http.ResponseWriter, r *http.Request) {
	me := s.authed(w, r)
	if me == nil {
		return
	}

	if !s.limiter.allow("key:"+me.UUID, 20, time.Hour) {
		fail(w, http.StatusTooManyRequests, "slow down")
		return
	}

	var body struct {
		PublicKey string `json:"publicKey"`
	}
	if !readBody(w, r, &body) {
		return
	}

	// Must be base64 and must be about the size of a P-256 SPKI. Not proof it is a key — only
	// the friend who fails to open a message would find that out — but enough that this field
	// cannot be used as somewhere to park arbitrary data.
	raw, err := base64.StdEncoding.DecodeString(body.PublicKey)
	if err != nil || len(raw) < 32 || len(raw) > 200 {
		fail(w, http.StatusBadRequest, "that is not a public key")
		return
	}

	if me.PublicKey == body.PublicKey {
		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
		return
	}

	me.PublicKey = body.PublicKey
	s.dirty = true

	// Friends need to see the new key before they can send anything readable, so this is worth
	// waking them for.
	s.bump()

	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}
