package main

import (
	"net/http"
	"strings"
	"time"
	"unicode/utf8"
)

// One message on its way from one friend to another.
//
// Deliberately not part of State. State is what saveNow marshals to state.json, and the one
// thing chat is not allowed to do is put messages on a disk — so they live on the Server itself,
// beside the auth handshakes, where nothing can serialise them by accident. A restart forgets
// every message in flight, which is the correct amount of chat history for a relay to keep.
type message struct {
	From string    `json:"from"`
	Name string    `json:"name"`
	Text string    `json:"text"`
	At   time.Time `json:"at"`
}

const (
	// Long enough for anything worth typing at somebody, short enough that a full inbox is a
	// few hundred kilobytes of memory rather than a problem.
	maxMessageRunes = 2000

	// Messages one person may have waiting. Past this the oldest goes: somebody who has not
	// opened Asobu in a week should not cost more than the last hundred things said to them.
	maxWaiting = 100

	// How long an uncollected message waits before it is forgotten. Chat here is for two people
	// who are both around; one that nobody came for in this long is better re-sent than
	// delivered out of nowhere hours later.
	messageLife = 10 * time.Minute

	// Per sender. One every two seconds is faster than anybody types for long, and well under
	// the 240-per-five-minutes every authenticated call already shares — deliberately, so a
	// sender who runs away hits this and not that. Tripping the shared limiter would take their
	// watch down with them, which turns one person's flood into their own outage.
	messagesPerMinute = 30

	// Messages held for everyone, everywhere. Each ceiling above bounds one conversation; this
	// bounds the process. At the sizes involved it is a few hundred megabytes before it bites,
	// which is far past anything real and far short of a machine with 3 GB free falling over.
	maxHeld = 20000
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
		To   string `json:"to"`
		Text string `json:"text"`
	}
	if !readBody(w, r, &body) {
		return
	}

	text := cleanMessage(body.Text)
	if text == "" {
		fail(w, http.StatusBadRequest, "nothing to send")
		return
	}
	if utf8.RuneCountInString(text) > maxMessageRunes {
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

	if s.held() >= maxHeld {
		fail(w, http.StatusServiceUnavailable, "too much in flight, try again shortly")
		return
	}

	waiting := s.inbox[body.To]
	if len(waiting) >= maxWaiting {
		waiting = waiting[len(waiting)-maxWaiting+1:]
	}

	s.inbox[body.To] = append(waiting, message{
		From: me.UUID,
		Name: me.Name,
		Text: text,
		At:   time.Now(),
	})

	// Wakes every open watch, the recipient's among them. That is the whole delivery mechanism:
	// their launcher is already holding a request open, and this is what makes it answer.
	s.bump()

	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

// cleanMessage trims a message and takes out the characters that are not text.
//
// Newlines and tabs stay — people write both on purpose. Everything else below a space goes:
// escape sequences, nulls and the rest are of no use to anyone typing and every use to somebody
// probing what a terminal or a text renderer does when handed them.
func cleanMessage(text string) string {
	var kept strings.Builder

	for _, r := range text {
		if r == '\n' || r == '\t' || r >= ' ' {
			kept.WriteRune(r)
		}
	}

	return strings.TrimSpace(kept.String())
}

// held counts every message waiting anywhere, for the ceiling on the process as a whole.
func (s *Server) held() int {
	total := 0
	for _, waiting := range s.inbox {
		total += len(waiting)
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
