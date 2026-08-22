package main

import (
	"net"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// Worlds people have open right now.
//
// Kept on Server rather than State, for the same reason chat is: nothing here may ever reach
// state.json. An address somebody was reachable at last Tuesday is not a thing this server should
// be able to produce, and keeping the type off State makes that structural rather than a promise.
//
// This server never carries a single byte of anyone's game. All it holds is where the host's door
// is and the pass they signed — enough for two launchers to find each other, and useless once they
// have. The pass is signed and checked by the host; a copy of everything here would not let this
// server, or anyone who took it, into anybody's world.
type hostedWorld struct {
	name    string
	players int
	max     int

	// What the world reports itself as. Passed along untouched — this server has no opinion on
	// which versions can play together, and the launcher that asks is the one that knows.
	version string

	// Stands for the world's version, loader and mod list together, so a friend holding a
	// matching instance can be sent straight in. Opaque here, like everything else.
	fingerprint string

	// Where the host's door might be reached: their own addresses as they see them, then the one
	// this server saw them arrive from. Ordered cheapest first — a friend on the same network, or
	// on the same VPN, connects without ever leaving it.
	addresses []string

	// Guest uuid -> the pass their host signed for them. Being in here is what "invited" means.
	invites map[string]string

	beat time.Time
}

// How long a world stays listed after the host last said it was still open. Comfortably more than
// the heartbeat, so one lost request does not take somebody's world off their friends' screens.
const hostingWindow = 45 * time.Second

// The most candidate addresses a host may offer. One LAN, one VPN, one public is the realistic
// case; the cap is only here so the list cannot be used as storage.
const mostAddresses = 6

// world returns the world this person has open, or nil. A world nobody has vouched for lately is
// forgotten as it is asked about, which is the only sweeping this needs: every online player's
// friends list asks about all of them every few seconds.
func (s *Server) world(uuid string) *hostedWorld {
	w := s.hosting[uuid]
	if w == nil {
		return nil
	}
	if time.Since(w.beat) > hostingWindow {
		delete(s.hosting, uuid)
		return nil
	}
	return w
}

// handleHostOpen is both "I have opened a world" and "it is still open". The launcher calls it
// every few seconds with the current player count; there is no separate heartbeat because the
// count is the thing that changes, and one request is fewer than two.
func (s *Server) handleHostOpen(w http.ResponseWriter, r *http.Request) {
	me := s.authed(w, r)
	if me == nil {
		return
	}
	var body struct {
		Name        string   `json:"name"`
		Players     int      `json:"players"`
		Max         int      `json:"max"`
		Port        int      `json:"port"`
		Version     string   `json:"version"`
		Fingerprint string   `json:"fingerprint"`
		Local       []string `json:"local"`
	}
	if !readBody(w, r, &body) {
		return
	}
	if body.Port < 1 || body.Port > 65535 {
		fail(w, http.StatusBadRequest, "that is not a port")
		return
	}

	addresses := cleanAddresses(body.Local)
	addresses = append(addresses, net.JoinHostPort(clientIP(r), strconv.Itoa(body.Port)))

	world := s.hosting[me.UUID]
	if world == nil {
		world = &hostedWorld{invites: map[string]string{}}
		s.hosting[me.UUID] = world
	}

	name := trimRunes(strings.TrimSpace(body.Name), 64)
	version := trimRunes(strings.TrimSpace(body.Version), 32)
	fingerprint := trimRunes(strings.TrimSpace(body.Fingerprint), 64)
	// Only wake the watchers when a friends list would actually read differently. A heartbeat
	// that says exactly what the last one said is not news, and this fires every few seconds.
	changed := world.name != name ||
		world.players != body.Players ||
		world.max != body.Max ||
		world.version != version ||
		world.fingerprint != fingerprint ||
		strings.Join(world.addresses, ",") != strings.Join(addresses, ",")

	world.name, world.players, world.max, world.version = name, body.Players, body.Max, version
	world.fingerprint = fingerprint
	world.addresses = addresses
	world.beat = time.Now()

	if changed {
		s.bump()
	}
	writeJSON(w, http.StatusOK, map[string]any{"addresses": addresses})
}

func (s *Server) handleHostClose(w http.ResponseWriter, r *http.Request) {
	me := s.authed(w, r)
	if me == nil {
		return
	}
	if _, open := s.hosting[me.UUID]; open {
		delete(s.hosting, me.UUID)
		s.bump()
	}
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

// handleHostInvite hands one friend the pass their host signed. The pass is opaque here — this
// server cannot read it, cannot check it, and cannot make another one.
func (s *Server) handleHostInvite(w http.ResponseWriter, r *http.Request) {
	me := s.authed(w, r)
	if me == nil {
		return
	}
	var body struct {
		UUID string `json:"uuid"`
		Pass string `json:"pass"`
	}
	if !readBody(w, r, &body) {
		return
	}
	world := s.world(me.UUID)
	if world == nil {
		fail(w, http.StatusConflict, "you do not have a world open")
		return
	}
	if len(body.Pass) == 0 || len(body.Pass) > 512 {
		fail(w, http.StatusBadRequest, "that is not a pass")
		return
	}

	guest := strings.ToLower(body.UUID)
	// Friends only. Someone who is not on your list has no business being handed the address of
	// your machine, and this is the only place that address is given out.
	if f, _ := s.between(me.UUID, guest); f == nil || !f.Accepted {
		fail(w, http.StatusForbidden, "you are not friends with them")
		return
	}

	world.invites[guest] = body.Pass
	s.bump()
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

// handleHostUninvite takes somebody off the list. Anyone already in the world stays there — this
// shuts the door rather than emptying the room.
//
// ponytail: a pass already handed out keeps working until it expires, which is why the launcher
// signs short ones. Add a revocation set on the door if that gap ever matters.
func (s *Server) handleHostUninvite(w http.ResponseWriter, r *http.Request) {
	me := s.authed(w, r)
	if me == nil {
		return
	}
	if world := s.world(me.UUID); world != nil {
		if _, invited := world.invites[strings.ToLower(r.PathValue("uuid"))]; invited {
			delete(world.invites, strings.ToLower(r.PathValue("uuid")))
			s.bump()
		}
	}
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

// cleanAddresses keeps the ones that are actually addresses. A host offers these, so they are
// somebody else's input; anything that is not a literal ip:port is dropped rather than passed on
// to a friend's launcher to try to make sense of.
func cleanAddresses(offered []string) []string {
	kept := []string{}
	for _, candidate := range offered {
		if len(kept) == mostAddresses {
			break
		}
		host, port, err := net.SplitHostPort(candidate)
		if err != nil || net.ParseIP(host) == nil {
			continue
		}
		if n, err := strconv.Atoi(port); err != nil || n < 1 || n > 65535 {
			continue
		}
		kept = append(kept, candidate)
	}
	return kept
}

// trimRunes cuts to a length in characters rather than bytes, so a world named in Cyrillic or
// Japanese is not cut through the middle of a letter.
func trimRunes(text string, most int) string {
	runes := []rune(text)
	if len(runes) <= most {
		return text
	}
	return string(runes[:most])
}
