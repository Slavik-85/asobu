// The Asobu API: identity and friends.
//
// Identity is proved the way Minecraft servers prove it. The launcher asks us for a random
// serverId, tells Mojang's session server it is "joining" that id with its own Minecraft token,
// and then asks us to check. We call Mojang's hasJoined with the same id and learn the player's
// UUID and name from Mojang — so this service never sees a Microsoft or Minecraft token, and a
// client cannot claim to be someone it isn't.
//
// Storage is one JSON file written atomically. ponytail: single process, whole-file saves —
// move to Postgres when the user count makes that embarrassing.
package main

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"regexp"
	"strings"
	"sync"
	"syscall"
	"time"
)

// ---------------------------------------------------------------------------- state

type User struct {
	UUID     string    `json:"uuid"` // dashless, lowercase, as Mojang reports it
	Name     string    `json:"name"`
	LastSeen time.Time `json:"lastSeen"`
}

// One row per pair. From is the requester; Accepted flips when the other side says yes.
type Friendship struct {
	From     string    `json:"from"`
	To       string    `json:"to"`
	Accepted bool      `json:"accepted"`
	Since    time.Time `json:"since"`
}

type Session struct {
	UUID    string    `json:"uuid"`
	Expires time.Time `json:"expires"`
}

type State struct {
	Users   map[string]*User    `json:"users"`   // uuid -> user
	Friends []*Friendship       `json:"friends"` //
	Tokens  map[string]*Session `json:"tokens"`  // sha256(token) hex -> session
}

type Server struct {
	mu    sync.Mutex
	state State
	path  string
	dirty bool

	// Auth handshakes in flight: serverId -> who asked and until when. Never persisted;
	// a restart mid-handshake just means signing in again.
	pending map[string]pendingAuth

	limiter limiter
}

type pendingAuth struct {
	name    string
	expires time.Time
}

// Seen within this window means online. The launcher heartbeats every 60 seconds.
const onlineWindow = 150 * time.Second

const tokenLife = 90 * 24 * time.Hour

// ---------------------------------------------------------------------------- persistence

func (s *Server) load() {
	s.state = State{Users: map[string]*User{}, Tokens: map[string]*Session{}}

	data, err := os.ReadFile(s.path)
	if err != nil {
		return // first run
	}
	if err := json.Unmarshal(data, &s.state); err != nil {
		log.Printf("state file unreadable, starting empty: %v", err)
	}
	if s.state.Users == nil {
		s.state.Users = map[string]*User{}
	}
	if s.state.Tokens == nil {
		s.state.Tokens = map[string]*Session{}
	}
}

// saveNow writes durable changes (users, friendships, tokens) immediately, through a rename so a
// crash mid-write never leaves half a file. Presence-only changes just mark dirty and ride the
// background flush — losing two minutes of lastSeen to a crash costs nothing.
func (s *Server) saveNow() {
	data, err := json.MarshalIndent(&s.state, "", " ")
	if err != nil {
		log.Printf("marshal state: %v", err)
		return
	}
	tmp := s.path + ".tmp"
	if err := os.WriteFile(tmp, data, 0o600); err != nil {
		log.Printf("write state: %v", err)
		return
	}
	if err := os.Rename(tmp, s.path); err != nil {
		log.Printf("rename state: %v", err)
		return
	}
	s.dirty = false
}

func (s *Server) flushLoop() {
	for range time.Tick(30 * time.Second) {
		s.mu.Lock()
		if s.dirty {
			s.saveNow()
		}
		// Expired sessions and stale handshakes go out with the tide.
		now := time.Now()
		for k, v := range s.state.Tokens {
			if now.After(v.Expires) {
				delete(s.state.Tokens, k)
				s.dirty = true
			}
		}
		for k, v := range s.pending {
			if now.After(v.expires) {
				delete(s.pending, k)
			}
		}
		s.mu.Unlock()
	}
}

// ---------------------------------------------------------------------------- rate limiting

// A window counter per key. Coarse on purpose: this exists to stop a loop hammering Mojang
// through us, not to be fair queueing.
type limiter struct {
	hits map[string][]time.Time
}

func (l *limiter) allow(key string, max int, window time.Duration) bool {
	if l.hits == nil {
		l.hits = map[string][]time.Time{}
	}
	now := time.Now()
	kept := l.hits[key][:0]
	for _, t := range l.hits[key] {
		if now.Sub(t) < window {
			kept = append(kept, t)
		}
	}
	if len(kept) >= max {
		l.hits[key] = kept
		return false
	}
	l.hits[key] = append(kept, now)
	return true
}

func clientIP(r *http.Request) string {
	// Caddy is the only thing that can reach us, and it sets X-Forwarded-For.
	if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
		return strings.TrimSpace(strings.Split(xff, ",")[0])
	}
	host, _, _ := net.SplitHostPort(r.RemoteAddr)
	return host
}

// ---------------------------------------------------------------------------- helpers

var namePattern = regexp.MustCompile(`^[A-Za-z0-9_]{1,16}$`)

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func fail(w http.ResponseWriter, status int, message string) {
	writeJSON(w, status, map[string]string{"error": message})
}

func readBody(w http.ResponseWriter, r *http.Request, v any) bool {
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(v); err != nil {
		fail(w, http.StatusBadRequest, "bad request body")
		return false
	}
	return true
}

func randomHex(bytes int) string {
	b := make([]byte, bytes)
	if _, err := rand.Read(b); err != nil {
		panic(err) // the OS entropy source failing is not a request-sized problem
	}
	return hex.EncodeToString(b)
}

func hashToken(token string) string {
	sum := sha256.Sum256([]byte(token))
	return hex.EncodeToString(sum[:])
}

// authed resolves the bearer token, bumps presence, and hands back the caller. Returns nil after
// having already written the 401.
func (s *Server) authed(w http.ResponseWriter, r *http.Request) *User {
	token, ok := strings.CutPrefix(r.Header.Get("Authorization"), "Bearer ")
	if !ok || token == "" {
		fail(w, http.StatusUnauthorized, "not signed in")
		return nil
	}
	session := s.state.Tokens[hashToken(token)]
	if session == nil || time.Now().After(session.Expires) {
		fail(w, http.StatusUnauthorized, "session expired")
		return nil
	}
	user := s.state.Users[session.UUID]
	if user == nil {
		fail(w, http.StatusUnauthorized, "unknown user")
		return nil
	}

	// Presence rides on every authenticated call; the session slides with use.
	user.LastSeen = time.Now()
	session.Expires = time.Now().Add(tokenLife)
	s.dirty = true
	return user
}

// between finds the friendship row linking two players, whoever asked first.
func (s *Server) between(a, b string) (*Friendship, int) {
	for i, f := range s.state.Friends {
		if (f.From == a && f.To == b) || (f.From == b && f.To == a) {
			return f, i
		}
	}
	return nil, -1
}

// ---------------------------------------------------------------------------- auth

func (s *Server) handleAuthBegin(w http.ResponseWriter, r *http.Request) {
	if !s.limiter.allow("auth:"+clientIP(r), 30, 5*time.Minute) {
		fail(w, http.StatusTooManyRequests, "slow down")
		return
	}
	var body struct {
		Name string `json:"name"`
	}
	if !readBody(w, r, &body) {
		return
	}
	if !namePattern.MatchString(body.Name) {
		fail(w, http.StatusBadRequest, "that is not a Minecraft username")
		return
	}

	serverId := "asobu" + randomHex(16)
	s.pending[serverId] = pendingAuth{name: body.Name, expires: time.Now().Add(time.Minute)}
	writeJSON(w, http.StatusOK, map[string]string{"serverId": serverId})
}

func (s *Server) handleAuthComplete(w http.ResponseWriter, r *http.Request) {
	var body struct {
		Name     string `json:"name"`
		ServerId string `json:"serverId"`
	}
	if !readBody(w, r, &body) {
		return
	}
	claim, ok := s.pending[body.ServerId]
	delete(s.pending, body.ServerId)
	if !ok || time.Now().After(claim.expires) || !strings.EqualFold(claim.name, body.Name) {
		fail(w, http.StatusBadRequest, "sign-in expired, try again")
		return
	}

	// The only outbound call this service makes. Mojang is the authority on who joined.
	// Unlocked while we wait: hasJoined can take a second, and holding the lock would stall
	// every other request behind one sign-in.
	s.mu.Unlock()
	profile, err := hasJoined(claim.name, body.ServerId)
	s.mu.Lock()
	if err != nil {
		fail(w, http.StatusBadGateway, "could not reach Mojang, try again")
		return
	}
	if profile == nil {
		fail(w, http.StatusUnauthorized, "Mojang did not confirm the sign-in")
		return
	}

	uuid := strings.ToLower(profile.Id)
	user := s.state.Users[uuid]
	if user == nil {
		user = &User{UUID: uuid}
		s.state.Users[uuid] = user
	}
	user.Name = profile.Name // Mojang's casing, and renames follow automatically
	user.LastSeen = time.Now()

	token := randomHex(32)
	s.state.Tokens[hashToken(token)] = &Session{UUID: uuid, Expires: time.Now().Add(tokenLife)}
	s.saveNow()

	writeJSON(w, http.StatusOK, map[string]string{"token": token, "uuid": uuid, "name": profile.Name})
}

type mojangProfile struct {
	Id   string `json:"id"`
	Name string `json:"name"`
}

var mojangClient = &http.Client{Timeout: 10 * time.Second}

func hasJoined(name, serverId string) (*mojangProfile, error) {
	resp, err := mojangClient.Get("https://sessionserver.mojang.com/session/minecraft/hasJoined?username=" +
		url.QueryEscape(name) + "&serverId=" + url.QueryEscape(serverId))
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK { // 204: nobody of that name joined that id
		return nil, nil
	}
	var profile mojangProfile
	if err := json.NewDecoder(resp.Body).Decode(&profile); err != nil {
		return nil, err
	}
	return &profile, nil
}

// ---------------------------------------------------------------------------- friends

type wireFriend struct {
	UUID     string    `json:"uuid"`
	Name     string    `json:"name"`
	Online   bool      `json:"online"`
	LastSeen time.Time `json:"lastSeen"`
}

func (s *Server) wire(uuid string) wireFriend {
	u := s.state.Users[uuid]
	if u == nil {
		return wireFriend{UUID: uuid, Name: "?"}
	}
	return wireFriend{UUID: u.UUID, Name: u.Name, Online: time.Since(u.LastSeen) < onlineWindow, LastSeen: u.LastSeen}
}

func (s *Server) handleFriendsList(w http.ResponseWriter, r *http.Request) {
	me := s.authed(w, r)
	if me == nil {
		return
	}

	friends, incoming, outgoing := []wireFriend{}, []wireFriend{}, []wireFriend{}
	for _, f := range s.state.Friends {
		switch {
		case f.Accepted && f.From == me.UUID:
			friends = append(friends, s.wire(f.To))
		case f.Accepted && f.To == me.UUID:
			friends = append(friends, s.wire(f.From))
		case f.To == me.UUID:
			incoming = append(incoming, s.wire(f.From))
		case f.From == me.UUID:
			outgoing = append(outgoing, s.wire(f.To))
		}
	}
	writeJSON(w, http.StatusOK, map[string]any{"friends": friends, "incoming": incoming, "outgoing": outgoing})
}

func (s *Server) handleFriendRequest(w http.ResponseWriter, r *http.Request) {
	me := s.authed(w, r)
	if me == nil {
		return
	}
	if !s.limiter.allow("req:"+me.UUID, 60, 5*time.Minute) {
		fail(w, http.StatusTooManyRequests, "slow down")
		return
	}
	var body struct {
		Name string `json:"name"`
	}
	if !readBody(w, r, &body) {
		return
	}

	var target *User
	for _, u := range s.state.Users {
		if strings.EqualFold(u.Name, body.Name) {
			target = u
			break
		}
	}
	if target == nil {
		fail(w, http.StatusNotFound, "no one by that name is on Asobu yet")
		return
	}
	if target.UUID == me.UUID {
		fail(w, http.StatusBadRequest, "that would be you")
		return
	}

	if existing, _ := s.between(me.UUID, target.UUID); existing != nil {
		// A request from someone who already asked us is both sides saying yes.
		if !existing.Accepted && existing.From == target.UUID {
			existing.Accepted = true
			s.saveNow()
		}
		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
		return
	}

	s.state.Friends = append(s.state.Friends, &Friendship{From: me.UUID, To: target.UUID, Since: time.Now()})
	s.saveNow()
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

func (s *Server) handleFriendAccept(w http.ResponseWriter, r *http.Request) {
	me := s.authed(w, r)
	if me == nil {
		return
	}
	var body struct {
		UUID string `json:"uuid"`
	}
	if !readBody(w, r, &body) {
		return
	}
	f, _ := s.between(me.UUID, strings.ToLower(body.UUID))
	if f == nil || f.To != me.UUID {
		fail(w, http.StatusNotFound, "no request from them")
		return
	}
	f.Accepted = true
	s.saveNow()
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

// One remover for all three shapes: unfriending, cancelling what I sent, declining what they sent.
func (s *Server) handleFriendRemove(w http.ResponseWriter, r *http.Request) {
	me := s.authed(w, r)
	if me == nil {
		return
	}
	other := strings.ToLower(r.PathValue("uuid"))
	if f, i := s.between(me.UUID, other); f != nil {
		s.state.Friends = append(s.state.Friends[:i], s.state.Friends[i+1:]...)
		s.saveNow()
	}
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

// ---------------------------------------------------------------------------- main

func main() {
	addr := os.Getenv("ASOBU_ADDR")
	if addr == "" {
		addr = "127.0.0.1:3000"
	}
	statePath := os.Getenv("ASOBU_STATE")
	if statePath == "" {
		statePath = "state.json"
	}

	s := &Server{path: statePath, pending: map[string]pendingAuth{}}
	s.load()
	go s.flushLoop()

	// One lock around every handler. ponytail: a mutex over a map is the whole concurrency
	// story until there are enough users to contend on it, which is a nice problem to have.
	locked := func(h http.HandlerFunc) http.HandlerFunc {
		return func(w http.ResponseWriter, r *http.Request) {
			s.mu.Lock()
			defer s.mu.Unlock()
			h(w, r)
		}
	}

	mux := http.NewServeMux()
	mux.HandleFunc("GET /v1/health", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
	})
	mux.HandleFunc("POST /v1/auth/begin", locked(s.handleAuthBegin))
	mux.HandleFunc("POST /v1/auth/complete", locked(s.handleAuthComplete))
	mux.HandleFunc("GET /v1/friends", locked(s.handleFriendsList))
	mux.HandleFunc("POST /v1/friends/requests", locked(s.handleFriendRequest))
	mux.HandleFunc("POST /v1/friends/accept", locked(s.handleFriendAccept))
	mux.HandleFunc("DELETE /v1/friends/{uuid}", locked(s.handleFriendRemove))

	// The state is saved on the way out, so a deploy never loses the last half minute.
	stop := make(chan os.Signal, 1)
	signal.Notify(stop, os.Interrupt, syscall.SIGTERM)
	go func() {
		<-stop
		s.mu.Lock()
		s.saveNow()
		os.Exit(0)
	}()

	log.Printf("asobu api on %s, state in %s", addr, statePath)
	log.Fatal(http.ListenAndServe(addr, mux))
}
