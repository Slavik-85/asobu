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
	"sort"
	"strconv"
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
	Shares  map[string]*Share   `json:"shares"`  // code -> shared instance, see shares.go
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

	// Bumped whenever anything a friends list shows changes. A watcher tells us the revision
	// it last saw; anything newer means it has something to collect.
	revision uint64

	// Closed and replaced on every bump, which is how one change wakes every waiter at once
	// without keeping a list of them. Waiters take a copy of the channel before releasing the
	// lock, so a bump landing in between still wakes them rather than being missed.
	changed chan struct{}
}

// bump wakes every watcher. Called with the lock held, after a change worth telling anyone about.
func (s *Server) bump() {
	s.revision++
	close(s.changed)
	s.changed = make(chan struct{})
}

type pendingAuth struct {
	name    string
	expires time.Time
}

// Seen within this window means online. The launcher heartbeats every 60 seconds.
const onlineWindow = 150 * time.Second

const tokenLife = 90 * 24 * time.Hour

// Ceilings on the things a caller can cause to accumulate. Each is far above what using Asobu
// normally looks like and far below what a script could manage in an afternoon.
const (
	// Handshakes waiting to be completed. They expire after a minute anyway; this is the guard
	// for the minute in between.
	maxPendingAuth = 5000

	// Sessions one account may hold. Signing in again is ordinary — on another machine, after
	// a reinstall — but a loop doing it should not grow the file forever.
	maxSessionsPerUser = 20

	// Friend requests one account may have outstanding at once. Accepting or being turned down
	// frees the slot; this only stops someone asking everybody.
	maxOutgoingRequests = 200
)

// ---------------------------------------------------------------------------- persistence

func (s *Server) load() {
	s.state = State{
		Users:  map[string]*User{},
		Tokens: map[string]*Session{},
		Shares: map[string]*Share{},
	}

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
	if s.state.Shares == nil {
		s.state.Shares = map[string]*Share{}
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

		// Codes whose week is up, removed rather than archived.
		s.dropExpiredShares(now)

		// And the rate-limit table, which otherwise keeps a row for every caller ever seen.
		s.limiter.forget(5 * time.Minute)

		s.mu.Unlock()
	}
}

// ---------------------------------------------------------------------------- rate limiting

// A window counter per key. Coarse on purpose: this exists to stop a loop hammering Mojang
// through us, not to be fair queueing.
type limiter struct {
	hits map[string][]time.Time
}

// The most callers to track at once. Past this the oldest are forgotten, which at worst gives
// a few strangers a fresh allowance — where growing without limit hands anyone who can vary
// their address a way to spend the server's memory instead.
const limiterKeyCap = 20000

// forget drops keys whose window has passed. Without it every address ever seen keeps an entry
// for the life of the process.
func (l *limiter) forget(window time.Duration) {
	now := time.Now()
	for key, times := range l.hits {
		if len(times) == 0 || now.Sub(times[len(times)-1]) >= window {
			delete(l.hits, key)
		}
	}

	// A sweep is not enough on its own: entries can be added faster than a window expires.
	if len(l.hits) > limiterKeyCap {
		for key := range l.hits {
			delete(l.hits, key)
			if len(l.hits) <= limiterKeyCap {
				break
			}
		}
	}
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

// clientIP returns the address the request really came from.
//
// The LAST entry of X-Forwarded-For, not the first. Caddy appends the peer it is talking to
// onto whatever header arrived, so a caller who sends "X-Forwarded-For: 1.2.3.4" produces
// "1.2.3.4, <their real address>" — and reading the first entry would take a value they chose.
// Every rate limit here is keyed on this, so trusting the front of that list would let anyone
// bypass all of them by inventing a new address per request.
//
// Only safe because exactly one proxy sits in front of us and we listen on loopback, so the
// last hop is always Caddy's view of the real peer.
func clientIP(r *http.Request) string {
	if xff := r.Header.Get("X-Forwarded-For"); xff != "" {
		parts := strings.Split(xff, ",")
		if last := strings.TrimSpace(parts[len(parts)-1]); last != "" {
			return last
		}
	}

	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
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

// randomHex returns cryptographically random hex, or "" if the OS refuses to provide any.
// Callers must treat "" as a failure: a predictable serverId or session token is worse than
// no sign-in at all.
func randomHex(bytes int) string {
	b := make([]byte, bytes)
	if _, err := rand.Read(b); err != nil {
		log.Printf("no randomness available: %v", err)
		return ""
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

	// Nothing authenticated is free. Generous enough that the launcher's once-a-minute
	// heartbeat and ordinary use never notice, tight enough that a loop does.
	if !s.limiter.allow("use:"+session.UUID, 240, 5*time.Minute) {
		fail(w, http.StatusTooManyRequests, "slow down")
		return nil
	}

	// Coming back from offline is news to whoever has you on their list. Every other presence
	// tick is not: a launcher left open would otherwise wake every watcher once a minute for
	// nothing.
	if time.Since(user.LastSeen) >= onlineWindow {
		s.bump()
	}

	// Presence rides on every authenticated call; the session slides with use.
	user.LastSeen = time.Now()
	session.Expires = time.Now().Add(tokenLife)
	s.dirty = true
	return user
}

// trimSessions keeps only the newest sessions for one account, dropping the rest. Signing in
// on a new machine should not cost the old one its session, but nothing needs twenty.
func (s *Server) trimSessions(uuid string) {
	var mine []string
	for hash, session := range s.state.Tokens {
		if session.UUID == uuid {
			mine = append(mine, hash)
		}
	}

	if len(mine) <= maxSessionsPerUser {
		return
	}

	// Expiry slides with use, so the ones expiring soonest are the ones least recently used.
	sort.Slice(mine, func(a, b int) bool {
		return s.state.Tokens[mine[a]].Expires.Before(s.state.Tokens[mine[b]].Expires)
	})

	for _, hash := range mine[:len(mine)-maxSessionsPerUser] {
		delete(s.state.Tokens, hash)
	}
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

	if len(s.pending) >= maxPendingAuth {
		fail(w, http.StatusServiceUnavailable, "too many sign-ins at once, try again in a moment")
		return
	}

	random := randomHex(16)
	if random == "" {
		fail(w, http.StatusInternalServerError, "could not start a sign-in, try again")
		return
	}

	serverId := "asobu" + random
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
	//
	// The re-lock is deferred rather than written after the call, so it happens even if that
	// call panics. Every handler here is wrapped in a deferred Unlock, and returning from this
	// one without the lock held would unlock an unlocked mutex — which panics in turn, and
	// leaves the server's locking in a state no later request can recover from.
	profile, err := func() (*mojangProfile, error) {
		s.mu.Unlock()
		defer s.mu.Lock()

		return hasJoined(claim.name, body.ServerId)
	}()
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
	if token == "" {
		fail(w, http.StatusInternalServerError, "could not finish the sign-in, try again")
		return
	}

	s.state.Tokens[hashToken(token)] = &Session{UUID: uuid, Expires: time.Now().Add(tokenLife)}
	s.trimSessions(uuid)
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
	if me := s.authed(w, r); me != nil {
		s.writeFriends(w, me)
	}
}

// writeFriends sends one person's whole social picture, and the revision it was true at.
//
// Split out so the watching form can authenticate once and answer once: going through the
// handler again would charge that caller's rate limit twice for a single request.
func (s *Server) writeFriends(w http.ResponseWriter, me *User) {
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
	writeJSON(w, http.StatusOK, map[string]any{
		"friends":  friends,
		"incoming": incoming,
		"outgoing": outgoing,
		"revision": s.revision,
	})
}

// How long a watch waits before answering anyway. Comfortably inside both this server's write
// timeout and Caddy's, so a quiet period ends with an answer rather than a dropped connection.
const watchWait = 20 * time.Second

// handleFriendsWatch answers as soon as anything changes, or after a quiet spell.
//
// This is what makes a friend request appear on the other screen without anyone reopening
// anything. The alternative was polling faster, which is the same request over and over to be
// told nothing has happened, and still slower than this.
func (s *Server) handleFriendsWatch(w http.ResponseWriter, r *http.Request) {
	me := s.authed(w, r)
	if me == nil {
		return
	}

	since, _ := strconv.ParseUint(r.URL.Query().Get("since"), 10, 64)

	// Waits only while the caller is exactly up to date. Behind means there is news to hand
	// over now; ahead means this server has restarted since they last asked, and its counter
	// began again at zero. Waiting in that second case would leave them holding a number this
	// server will not reach for a long time, hearing nothing in the meantime.
	if s.revision == since {
		// Taken before the lock is released, so a change landing in the gap still wakes this.
		changed := s.changed

		func() {
			s.mu.Unlock()
			defer s.mu.Lock()

			select {
			case <-changed:
			case <-time.After(watchWait):
			case <-r.Context().Done():
			}
		}()
	}

	s.writeFriends(w, me)
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
			s.bump()
			s.saveNow()
		}
		writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
		return
	}

	outstanding := 0
	for _, f := range s.state.Friends {
		if !f.Accepted && f.From == me.UUID {
			outstanding++
		}
	}
	if outstanding >= maxOutgoingRequests {
		fail(w, http.StatusTooManyRequests, "too many requests waiting on a reply")
		return
	}

	s.state.Friends = append(s.state.Friends, &Friendship{From: me.UUID, To: target.UUID, Since: time.Now()})
	s.bump()
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
	s.bump()
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
		s.bump()
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

	s := &Server{path: statePath, pending: map[string]pendingAuth{}, changed: make(chan struct{})}
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
	mux.HandleFunc("GET /v1/friends/watch", locked(s.handleFriendsWatch))
	mux.HandleFunc("POST /v1/friends/requests", locked(s.handleFriendRequest))
	mux.HandleFunc("POST /v1/friends/accept", locked(s.handleFriendAccept))
	mux.HandleFunc("DELETE /v1/friends/{uuid}", locked(s.handleFriendRemove))
	mux.HandleFunc("POST /v1/shares", locked(s.handleShareCreate))
	mux.HandleFunc("GET /v1/shares/{code}", locked(s.handleShareRead))
	mux.HandleFunc("DELETE /v1/shares/{code}", locked(s.handleShareDelete))

	// The state is saved on the way out, so a deploy never loses the last half minute.
	stop := make(chan os.Signal, 1)
	signal.Notify(stop, os.Interrupt, syscall.SIGTERM)
	go func() {
		<-stop
		s.mu.Lock()
		s.saveNow()
		os.Exit(0)
	}()

	// Without these a caller can open connections and send a byte at a time, holding one
	// goroutine each for as long as it likes and costing nothing to do it.
	server := &http.Server{
		Addr:              addr,
		Handler:           mux,
		ReadHeaderTimeout: 10 * time.Second,
		ReadTimeout:       20 * time.Second,
		WriteTimeout:      30 * time.Second,
		IdleTimeout:       60 * time.Second,
		MaxHeaderBytes:    16 << 10,
	}

	log.Printf("asobu api on %s, state in %s", addr, statePath)
	log.Fatal(server.ListenAndServe())
}
