package main

import (
	"context"
	"encoding/json"
	"net"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/coder/websocket"
)

// The relay: how two people play together when neither can reach the other.
//
// A direct connection needs one side to be reachable from the internet, and on a home router
// nothing is. Both ends here only ever dial out, which every network allows, and this pipes the
// two together. It is the path that always works; hole punching, when it exists, will be the
// optimisation that keeps most traffic off this server.
//
// It rides the same 443 as the rest of the API, as a WebSocket through Caddy. That is not only to
// avoid opening a port: an odd port is the first thing a school or office network blocks, and
// "works from anywhere" has to mean anywhere.
//
// This server sees the bytes but cannot read them: past the handshake it is Minecraft's own
// protocol between two clients, and the pass that decided whether the guest gets in was checked
// by the host's doorman before any of it.

// One person hosting, waiting for guests.
type relaySession struct {
	owner   string
	control *websocket.Conn

	// Bytes carried for this session, both directions. Not a rate limit: a session that has moved
	// this much is either a very long evening or something that has stopped being a game.
	carried atomic.Int64

	opened time.Time
}

// A guest waiting for the host to dial back, and the socket they arrived on.
type relayTicket struct {
	guest  net.Conn
	joined chan net.Conn
}

const (
	// A session that has moved this much is closed. Two people playing for an evening are far
	// under it; a loop that has stopped being Minecraft is not.
	relayCeiling = 4 << 30

	// Sessions at once. The cost of this feature is bandwidth, and this is the ceiling on how
	// many ways it can be spent at the same time.
	relayMostSessions = 200

	// How long a guest waits for the host to dial back before giving up.
	relayHandshake = 15 * time.Second
)

type relayHub struct {
	mu       sync.Mutex
	sessions map[string]*relaySession
	tickets  map[string]*relayTicket
}

func newRelayHub() *relayHub {
	return &relayHub{
		sessions: map[string]*relaySession{},
		tickets:  map[string]*relayTicket{},
	}
}

// handleReflect tells a caller the address the internet sees them at, port included.
//
// Punching needs this. A router rewrites the source port of everything leaving it, and neither
// end can know what it was rewritten to without being told by somebody outside. The caller dials
// this from the very socket it means to punch with, so the address that comes back is the one
// that socket is reachable at for as long as the mapping lives.
//
// It reveals nothing the caller did not already tell us by connecting, so it needs no sign-in.
func (s *Server) handleReflect(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]string{
		"address": net.JoinHostPort(clientIP(r), clientPort(r)),
	})
}

// clientPort is the source port the caller arrived on. Caddy is what the internet connects to, so
// the socket this process sees is Caddy's; the real one is passed along in a header it is
// configured to add.
func clientPort(r *http.Request) string {
	// Checked rather than trusted: a misconfigured proxy passes the placeholder through as text,
	// and an address ending in something that is not a number is worse than no answer, because it
	// looks like one.
	forwarded := strings.TrimSpace(r.Header.Get("X-Forwarded-Port"))
	if n, err := strconv.Atoi(forwarded); err == nil && n > 0 && n <= 65535 {
		return forwarded
	}
	if _, port, err := net.SplitHostPort(r.RemoteAddr); err == nil {
		return port
	}
	return "0"
}

// handleRelayPunch asks a host to fire at a guest, so the guest's connection is let through the
// host's router instead of being dropped as unsolicited.
//
// The relay session doubles as the way to reach the host, since it is already open whenever
// anybody is hosting and the guest already knows its name.
func (s *Server) handleRelayPunch(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Session string `json:"session"`
	}
	if !readBody(w, r, &body) {
		return
	}

	// Where to fire is taken from the connection rather than from the body. A caller that could
	// name any address would be a way to have a stranger's machine send traffic wherever they
	// liked, and asking them is pointless anyway when this end can see it.
	address := net.JoinHostPort(clientIP(r), clientPort(r))

	s.relay.mu.Lock()
	session := s.relay.sessions[body.Session]
	s.relay.mu.Unlock()

	if session == nil {
		fail(w, http.StatusNotFound, "that world is not being relayed")
		return
	}

	asking, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	if err := writeRelayJSON(asking, session.control, map[string]string{"punch": address}); err != nil {
		fail(w, http.StatusBadGateway, "could not reach them")
		return
	}

	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

// handleRelay is the whole relay, told apart by what the caller says it is.
//
// Deliberately outside the one big lock the rest of the API runs under: a connection here is held
// open for as long as somebody is playing, and holding that lock for an evening would stop
// everything else.
func (s *Server) handleRelay(w http.ResponseWriter, r *http.Request) {
	switch r.URL.Query().Get("role") {
	case "host":
		s.relayHost(w, r)
	case "guest":
		s.relayGuest(w, r)
	case "tunnel":
		s.relayTunnel(w, r)
	default:
		fail(w, http.StatusBadRequest, "no such role")
	}
}

// accept upgrades the connection. Origin checking is off because the caller is a launcher rather
// than a browser, and there is no page for another site to embed.
func acceptRelay(w http.ResponseWriter, r *http.Request) (*websocket.Conn, error) {
	return websocket.Accept(w, r, &websocket.AcceptOptions{InsecureSkipVerify: true})
}

// relayHost registers somebody as reachable and holds the line open. Everything they are told
// afterwards comes down this connection; closing it ends their session.
func (s *Server) relayHost(w http.ResponseWriter, r *http.Request) {
	me := s.authedFor(r)
	if me == "" {
		fail(w, http.StatusUnauthorized, "not signed in")
		return
	}

	s.relay.mu.Lock()
	if len(s.relay.sessions) >= relayMostSessions {
		s.relay.mu.Unlock()
		fail(w, http.StatusServiceUnavailable, "too many worlds are being relayed right now")
		return
	}
	s.relay.mu.Unlock()

	control, err := acceptRelay(w, r)
	if err != nil {
		return
	}

	id := randomHex(9)
	session := &relaySession{owner: me, control: control, opened: time.Now()}

	s.relay.mu.Lock()
	s.relay.sessions[id] = session
	s.relay.mu.Unlock()

	defer func() {
		s.relay.mu.Lock()
		delete(s.relay.sessions, id)
		s.relay.mu.Unlock()
		control.Close(websocket.StatusNormalClosure, "")
	}()

	if err := writeRelayJSON(r.Context(), control, map[string]string{"session": id}); err != nil {
		return
	}

	// Nothing else is expected from the host; this reads only so that a dropped connection is
	// noticed and the session goes with it.
	for {
		if _, _, err := control.Read(r.Context()); err != nil {
			return
		}
	}
}

// relayGuest takes somebody who wants in, asks the host to dial back, and pairs the two.
func (s *Server) relayGuest(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("session")

	s.relay.mu.Lock()
	session := s.relay.sessions[id]
	s.relay.mu.Unlock()

	if session == nil {
		fail(w, http.StatusNotFound, "that world is not being relayed")
		return
	}

	socket, err := acceptRelay(w, r)
	if err != nil {
		return
	}

	guest := websocket.NetConn(context.Background(), socket, websocket.MessageBinary)
	defer guest.Close()

	ticket := randomHex(12)
	waiting := &relayTicket{guest: guest, joined: make(chan net.Conn, 1)}

	s.relay.mu.Lock()
	s.relay.tickets[ticket] = waiting
	s.relay.mu.Unlock()

	defer func() {
		s.relay.mu.Lock()
		delete(s.relay.tickets, ticket)
		s.relay.mu.Unlock()
	}()

	asking, cancel := context.WithTimeout(context.Background(), relayHandshake)
	defer cancel()

	if err := writeRelayJSON(asking, session.control, map[string]string{"open": ticket}); err != nil {
		return
	}

	select {
	case host := <-waiting.joined:
		defer host.Close()
		relayPipe(session, guest, host)
	case <-asking.Done():
		// The host never came back. Their launcher may have closed between the friends list
		// saying the world was open and this moment.
	}
}

// relayTunnel is the host dialling back for one particular guest.
func (s *Server) relayTunnel(w http.ResponseWriter, r *http.Request) {
	ticket := r.URL.Query().Get("ticket")

	s.relay.mu.Lock()
	waiting := s.relay.tickets[ticket]
	if waiting != nil {
		delete(s.relay.tickets, ticket)
	}
	s.relay.mu.Unlock()

	if waiting == nil {
		fail(w, http.StatusNotFound, "nobody is waiting on that")
		return
	}

	socket, err := acceptRelay(w, r)
	if err != nil {
		return
	}

	host := websocket.NetConn(context.Background(), socket, websocket.MessageBinary)
	waiting.joined <- host

	// Held open here rather than returned: the handler ending would close the connection out from
	// under the pipe. It ends when the guest's side does.
	<-r.Context().Done()
}

// relayPipe moves bytes until one side stops, or until the session has carried more than anybody
// should be carrying through somebody else's server.
func relayPipe(session *relaySession, guest, host net.Conn) {
	done := make(chan struct{}, 2)

	move := func(to, from net.Conn) {
		defer func() { done <- struct{}{} }()

		buffer := make([]byte, 32<<10)
		for {
			read, err := from.Read(buffer)
			if read > 0 {
				if session.carried.Add(int64(read)) > relayCeiling {
					return
				}
				if _, err := to.Write(buffer[:read]); err != nil {
					return
				}
			}
			if err != nil {
				return
			}
		}
	}

	go move(host, guest)
	go move(guest, host)

	<-done
}

func writeRelayJSON(ctx context.Context, c *websocket.Conn, payload map[string]string) error {
	body, err := json.Marshal(payload)
	if err != nil {
		return err
	}
	return c.Write(ctx, websocket.MessageText, body)
}

// authedFor is the bearer check without the rest of what authed does. The relay runs outside the
// server's one big lock, so it takes the lock only long enough to look the token up.
func (s *Server) authedFor(r *http.Request) string {
	token := r.Header.Get("Authorization")
	if len(token) < 8 || token[:7] != "Bearer " {
		return ""
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	session := s.state.Tokens[hashToken(token[7:])]
	if session == nil || time.Now().After(session.Expires) {
		return ""
	}
	return session.UUID
}
