package main

// Offline accounts on the friends network.
//
// Everywhere else, identity here is Mojang's: a launcher joins a serverId, Mojang confirms who
// joined it, and the name that comes back is a name nobody else can be using. An offline account
// has none of that. It is a string somebody typed, and two people who type "Steve" are, as far
// as anything can tell, the same person.
//
// So the server names them instead. An offline account is issued four random digits and is found
// as name#tag — the name is theirs to choose and the tag is not, which is what makes the pair
// unique without anyone having to prove anything. It is deliberately a weaker claim than a
// Mojang account's, and it is worth being clear that it is: a tag says the server handed this
// out, not that the person holding it is anybody in particular.
//
// What stops that being free is the ledger below: five accounts per address and five per
// machine. Neither is stored. Both are counted through a keyed hash whose salt lives only in
// state.json, so the file records that some machine made three accounts without recording which.

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"math/big"
	"net/http"
	"strings"
	"time"

	crand "crypto/rand"
)

// How many offline accounts one address, or one machine, may bring onto the network.
const maxOfflinePerOrigin = 5

// Tries at a free tag before giving up. Ten thousand tags to a name and four digits' worth of
// room; a name would have to be extraordinarily popular for this to matter, and giving up with
// an honest message beats looping.
const tagAttempts = 40

// originKey turns an address or a machine id into something countable and nothing else.
//
// Keyed with the server's own salt rather than plainly hashed: a bare sha256 of an IPv4 address
// is not anonymous, it is a four-billion-entry lookup anybody can build in an afternoon. With a
// secret in the mix, the ledger is meaningless to anyone who does not already have the salt, and
// the salt is worth nothing on its own.
func (s *Server) originKey(kind, value string) string {
	mac := hmac.New(sha256.New, []byte(s.state.Salt))
	mac.Write([]byte(kind))
	mac.Write([]byte{0})
	mac.Write([]byte(strings.ToLower(strings.TrimSpace(value))))
	return hex.EncodeToString(mac.Sum(nil))[:32]
}

// randomTag is four digits, zero-padded, chosen properly at random rather than from the clock.
func randomTag() string {
	n, err := crand.Int(crand.Reader, big.NewInt(10000))
	if err != nil {
		return ""
	}
	digits := n.String()
	return strings.Repeat("0", 4-len(digits)) + digits
}

// takenTag reports whether this exact name#tag already belongs to somebody.
func (s *Server) takenTag(name, tag string) bool {
	for _, u := range s.state.Users {
		if u.Tag == tag && strings.EqualFold(u.Name, name) {
			return true
		}
	}
	return false
}

// findByHandle resolves what somebody typed into the account they meant.
//
// A bare name only ever matches an account Mojang vouches for, and name#tag only ever matches an
// offline one. That split is the point: without it, typing "Steve" could land on whichever of the
// two the map happened to yield first, and adding a friend would be a coin toss.
func (s *Server) findByHandle(handle string) *User {
	handle = strings.TrimSpace(handle)

	if name, tag, ok := strings.Cut(handle, "#"); ok {
		for _, u := range s.state.Users {
			if u.Tag != "" && u.Tag == strings.TrimSpace(tag) && strings.EqualFold(u.Name, strings.TrimSpace(name)) {
				return u
			}
		}
		return nil
	}

	for _, u := range s.state.Users {
		if u.Tag == "" && strings.EqualFold(u.Name, handle) {
			return u
		}
	}
	return nil
}

// handleOfflineJoin puts an offline account on the network, or hands back one already there.
//
// Deliberately something a person asks for rather than something their launcher does on their
// behalf. An offline account is a local thing until the moment somebody presses the button, and
// a launcher that quietly announced every name anyone had ever played under would be publishing
// a list nobody asked it to publish.
func (s *Server) handleOfflineJoin(w http.ResponseWriter, r *http.Request) {
	ip := clientIP(r)

	// Ahead of any of the work below, and per address, because this is the one route here that
	// makes an account out of nothing but a request.
	if !s.limiter.allow("offline:"+ip, 12, time.Hour) {
		fail(w, http.StatusTooManyRequests, "too many tries from here, give it an hour")
		return
	}

	var body struct {
		Name string `json:"name"`
		HWID string `json:"hwid"`
		UUID string `json:"uuid"` // optional: an account this machine already holds
	}
	if !readBody(w, r, &body) {
		return
	}

	body.Name = strings.TrimSpace(body.Name)
	if !namePattern.MatchString(body.Name) {
		fail(w, http.StatusBadRequest, "that name has characters Minecraft would not take")
		return
	}
	if len(body.HWID) < 16 || len(body.HWID) > 128 {
		fail(w, http.StatusBadRequest, "this launcher did not identify its machine")
		return
	}

	hwKey := s.originKey("hw", body.HWID)
	ipKey := s.originKey("ip", ip)

	// Coming back rather than arriving. A reinstall, a second launcher, the same person: they
	// keep the account they had and spend none of their five on it. The machine has to match,
	// or a uuid would be all anyone needed to walk into somebody else's account.
	if body.UUID != "" {
		if existing := s.state.Users[body.UUID]; existing != nil && existing.Tag != "" && existing.HWIDKey == hwKey {
			s.issueOffline(w, existing)
			return
		}
	}

	// Both ledgers, counted before either is written to. An account already listed under this
	// machine is not a new one; the check above handles that case, so anything reaching here is
	// somebody asking for one more.
	if len(s.state.OfflineHWIDs[hwKey]) >= maxOfflinePerOrigin {
		fail(w, http.StatusForbidden,
			"this computer has already put five offline accounts on the network")
		return
	}
	if len(s.state.OfflineIPs[ipKey]) >= maxOfflinePerOrigin {
		fail(w, http.StatusForbidden,
			"this connection has already put five offline accounts on the network")
		return
	}

	tag := ""
	for i := 0; i < tagAttempts; i++ {
		candidate := randomTag()
		if candidate == "" {
			fail(w, http.StatusInternalServerError, "could not make a tag, try again")
			return
		}
		if !s.takenTag(body.Name, candidate) {
			tag = candidate
			break
		}
	}
	if tag == "" {
		fail(w, http.StatusConflict, "that name has run out of tags — try a different one")
		return
	}

	// Its own identifier, not the one Minecraft derives from the name. That one is a hash of the
	// name and nothing else, so every "Steve" in the world shares it — as a key here it would
	// make them all one account, reading each other's messages.
	uuid := randomHex(16)
	if uuid == "" {
		fail(w, http.StatusInternalServerError, "could not make an account, try again")
		return
	}

	user := &User{
		UUID:     uuid,
		Name:     body.Name,
		Tag:      tag,
		HWIDKey:  hwKey,
		LastSeen: time.Now(),
	}
	s.state.Users[uuid] = user
	s.state.OfflineHWIDs[hwKey] = append(s.state.OfflineHWIDs[hwKey], uuid)
	s.state.OfflineIPs[ipKey] = append(s.state.OfflineIPs[ipKey], uuid)

	s.issueOffline(w, user)
}

// issueOffline hands back a session, the same shape the Mojang route ends with plus the tag.
func (s *Server) issueOffline(w http.ResponseWriter, user *User) {
	token := randomHex(32)
	if token == "" {
		fail(w, http.StatusInternalServerError, "could not finish joining, try again")
		return
	}

	user.LastSeen = time.Now()
	s.state.Tokens[hashToken(token)] = &Session{UUID: user.UUID, Expires: time.Now().Add(tokenLife)}
	s.trimSessions(user.UUID)
	s.saveNow()

	writeJSON(w, http.StatusOK, map[string]string{
		"token":  token,
		"uuid":   user.UUID,
		"name":   user.Name,
		"tag":    user.Tag,
		"handle": user.Handle(),
	})
}
