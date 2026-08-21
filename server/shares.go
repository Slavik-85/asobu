// Share codes: a short string that stands for one instance's contents.
//
// What a share holds is a list of files identified by hash, never by URL. Whoever imports the
// code looks each hash up at Modrinth or CurseForge and downloads from there. That is a
// deliberate limit rather than a convenience: a share that could name its own download address
// would be a way to make someone else's launcher fetch a file of the sharer's choosing, and no
// amount of validation elsewhere would make that safe.
//
// Codes are content-addressed. The server hashes the manifest itself and reuses the existing
// code whenever that hash already exists, which is what makes asking twice return the same code
// and makes a copied instance share as its original. The client never sends the hash, so it
// cannot claim to be content it does not have.
package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"path"
	"regexp"
	"sort"
	"strings"
	"time"
)

// How long a code lives, and how long it lives again each time the same content is shared.
const shareLife = 7 * 24 * time.Hour

const (
	// Active codes one account may hold. Sharing is cheap for the sharer and costs us storage,
	// so there is a ceiling; it is far above what anyone sharing their own instances will hit.
	maxSharesPerUser = 50

	// A manifest is a list of hashes, so this is generous. Five hundred files of about 120
	// bytes each is 60 KB; the rest is headroom for long names.
	maxShareBody = 256 << 10

	maxShareFiles = 500

	// One file cannot claim to be larger than this. Nothing in a Minecraft instance that is
	// worth sharing by hash comes close, and the number stops a manifest describing a download
	// that would fill someone's disk.
	maxShareFileSize = 512 << 20
)

// Codes people read off a screen and type into another machine. No 0/O and no 1/I/l, because
// they are the same character to someone copying by eye.
const codeAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ"

const codeLength = 8

// The only places a shared file may land. Everything here is content that can be looked up by
// hash and downloaded from a mod host; anything else in an instance is either the game itself
// or personal configuration nobody meant to publish.
var shareFolders = []string{"mods", "resourcepacks", "shaderpacks", "datapacks"}

var (
	sha1Pattern    = regexp.MustCompile(`^[0-9a-f]{40}$`)
	versionPattern = regexp.MustCompile(`^[A-Za-z0-9._+-]{1,32}$`)
	loaderPattern  = regexp.MustCompile(`^(vanilla|fabric|quilt|forge|neoforge)$`)
)

// ShareFile is one downloadable thing, named by where it goes and what it hashes to.
type ShareFile struct {
	Path string `json:"path"`
	Sha1 string `json:"sha1"`
	Size int64  `json:"size"`

	// CurseForge's own fingerprint, which is how their catalogue is searched by file. Zero
	// where the sharer could not compute one; the SHA-1 still finds it on Modrinth.
	Fingerprint uint32 `json:"fingerprint"`
}

// ShareManifest is everything needed to rebuild an instance, minus the files themselves.
type ShareManifest struct {
	Name          string      `json:"name"`
	GameVersion   string      `json:"gameVersion"`
	Loader        string      `json:"loader"`
	LoaderVersion string      `json:"loaderVersion"`
	Files         []ShareFile `json:"files"`
}

type Share struct {
	Code string `json:"code"`

	// SHA-256 of the manifest's contents, excluding its name. Two instances holding the same
	// files share a code however they are named, which is what makes a copy share as the
	// original.
	Fingerprint string `json:"fingerprint"`

	Owner    string        `json:"owner"`
	Manifest ShareManifest `json:"manifest"`
	Created  time.Time     `json:"created"`
	Expires  time.Time     `json:"expires"`
}

// ---------------------------------------------------------------------------- validation

// clean returns the manifest as it will be stored, or an explanation of why it will not be.
//
// Everything is rebuilt into known fields rather than kept as it arrived, so nothing a caller
// invents survives into what other people download.
func clean(in ShareManifest) (ShareManifest, string) {
	out := ShareManifest{
		Name:          strings.TrimSpace(in.Name),
		GameVersion:   strings.TrimSpace(in.GameVersion),
		Loader:        strings.ToLower(strings.TrimSpace(in.Loader)),
		LoaderVersion: strings.TrimSpace(in.LoaderVersion),
	}

	if n := len([]rune(out.Name)); n == 0 || n > 64 {
		return out, "an instance name is required, up to 64 characters"
	}
	if strings.ContainsFunc(out.Name, func(r rune) bool { return r < 0x20 || r == 0x7f }) {
		return out, "that instance name contains characters that cannot be shared"
	}
	if !versionPattern.MatchString(out.GameVersion) {
		return out, "that is not a Minecraft version"
	}
	if !loaderPattern.MatchString(out.Loader) {
		return out, "unknown mod loader"
	}
	if out.LoaderVersion != "" && !versionPattern.MatchString(out.LoaderVersion) {
		return out, "that is not a loader version"
	}
	if len(in.Files) > maxShareFiles {
		return out, "too many files in that instance to share"
	}

	seen := make(map[string]bool, len(in.Files))
	for _, f := range in.Files {
		p, why := cleanPath(f.Path)
		if why != "" {
			return out, why
		}
		if seen[strings.ToLower(p)] {
			return out, "the same file appears twice"
		}
		seen[strings.ToLower(p)] = true

		if !sha1Pattern.MatchString(strings.ToLower(f.Sha1)) {
			return out, "a file is missing a valid SHA-1"
		}
		if f.Size <= 0 || f.Size > maxShareFileSize {
			return out, "a file claims an impossible size"
		}

		out.Files = append(out.Files, ShareFile{
			Path:        p,
			Sha1:        strings.ToLower(f.Sha1),
			Size:        f.Size,
			Fingerprint: f.Fingerprint,
		})
	}

	return out, ""
}

// cleanPath returns a path that can only ever land inside the instance, or why it cannot.
//
// The launcher checks this again before writing anything, because a server it does not control
// is not something to take a file path from. Checking here as well means a hostile manifest is
// refused when it is created rather than by each person who imports it.
func cleanPath(raw string) (string, string) {
	p := strings.TrimSpace(strings.ReplaceAll(raw, "\\", "/"))

	if p == "" || len(p) > 200 {
		return "", "a file path is empty or too long"
	}
	if strings.ContainsFunc(p, func(r rune) bool { return r < 0x20 || r == 0x7f }) {
		return "", "a file path contains control characters"
	}
	if strings.HasPrefix(p, "/") || strings.Contains(p, ":") {
		return "", "a file path must be relative"
	}

	// path.Clean resolves any "." and ".." it can; anything left over is trying to climb out.
	p = path.Clean(p)
	if p == "." || strings.HasPrefix(p, "../") || p == ".." {
		return "", "a file path points outside the instance"
	}

	folder, rest, found := strings.Cut(p, "/")
	if !found || rest == "" {
		return "", "a file must be inside one of the instance's content folders"
	}

	allowed := false
	for _, f := range shareFolders {
		if strings.EqualFold(folder, f) {
			allowed = true
			break
		}
	}
	if !allowed {
		return "", "only mods, resource packs, shaders and data packs can be shared by code"
	}

	return p, ""
}

// fingerprintOf hashes what the manifest contains, ignoring what it is called.
//
// Computed here rather than accepted from the caller: dedup decides which code someone gets
// back, so a caller who could name the hash could ask for a code belonging to content they do
// not have.
func fingerprintOf(m ShareManifest) string {
	lines := make([]string, 0, len(m.Files)+1)
	lines = append(lines, strings.ToLower(m.GameVersion+"|"+m.Loader+"|"+m.LoaderVersion))

	for _, f := range m.Files {
		lines = append(lines, strings.ToLower(f.Path)+"|"+f.Sha1)
	}

	// Sorted so the same set of files hashes the same however they were listed.
	sort.Strings(lines[1:])

	sum := sha256.Sum256([]byte(strings.Join(lines, "\n")))
	return hex.EncodeToString(sum[:])
}

// ---------------------------------------------------------------------------- handlers

func (s *Server) handleShareCreate(w http.ResponseWriter, r *http.Request) {
	me := s.authed(w, r)
	if me == nil {
		return
	}
	if !s.limiter.allow("share:"+me.UUID, 30, 5*time.Minute) {
		fail(w, http.StatusTooManyRequests, "slow down")
		return
	}

	var body ShareManifest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, maxShareBody)).Decode(&body); err != nil {
		fail(w, http.StatusBadRequest, "that instance could not be read")
		return
	}

	manifest, why := clean(body)
	if why != "" {
		fail(w, http.StatusBadRequest, why)
		return
	}

	fingerprint := fingerprintOf(manifest)
	now := time.Now()

	// Same contents as something already shared: the same code comes back with its clock wound
	// forward. Whoever shared it first keeps ownership, and the name they used is the one that
	// stays, because it is their share.
	for _, existing := range s.state.Shares {
		if existing.Fingerprint == fingerprint && now.Before(existing.Expires) {
			existing.Expires = now.Add(shareLife)
			s.saveNow()

			writeJSON(w, http.StatusOK, map[string]any{
				"code":    existing.Code,
				"expires": existing.Expires,
				"reused":  true,
			})
			return
		}
	}

	mine := 0
	for _, existing := range s.state.Shares {
		if existing.Owner == me.UUID && now.Before(existing.Expires) {
			mine++
		}
	}
	if mine >= maxSharesPerUser {
		fail(w, http.StatusTooManyRequests, "too many share codes at once, wait for some to expire")
		return
	}

	code := s.freeCode()
	if code == "" {
		fail(w, http.StatusInternalServerError, "could not make a code, try again")
		return
	}

	s.state.Shares[code] = &Share{
		Code:        code,
		Fingerprint: fingerprint,
		Owner:       me.UUID,
		Manifest:    manifest,
		Created:     now,
		Expires:     now.Add(shareLife),
	}
	s.saveNow()

	writeJSON(w, http.StatusOK, map[string]any{
		"code":    code,
		"expires": now.Add(shareLife),
		"reused":  false,
	})
}

// handleShareRead hands back a manifest. Deliberately open to anyone holding the code: being
// sent a pack should not require making an account first.
func (s *Server) handleShareRead(w http.ResponseWriter, r *http.Request) {
	if !s.limiter.allow("shareget:"+clientIP(r), 120, 5*time.Minute) {
		fail(w, http.StatusTooManyRequests, "slow down")
		return
	}

	code := strings.ToUpper(strings.TrimSpace(r.PathValue("code")))

	share, ok := s.state.Shares[code]
	if !ok || time.Now().After(share.Expires) {
		// One answer for both, so this cannot be used to learn which codes exist.
		fail(w, http.StatusNotFound, "that code has expired or never existed")
		return
	}

	writeJSON(w, http.StatusOK, map[string]any{
		"manifest": share.Manifest,
		"expires":  share.Expires,
	})
}

// handleShareDelete withdraws a code early. Only whoever created it may.
func (s *Server) handleShareDelete(w http.ResponseWriter, r *http.Request) {
	me := s.authed(w, r)
	if me == nil {
		return
	}

	code := strings.ToUpper(strings.TrimSpace(r.PathValue("code")))

	// Someone else's code is answered exactly like a code that is not there, so this cannot be
	// used to find out whether one exists.
	if share, ok := s.state.Shares[code]; ok && share.Owner == me.UUID {
		delete(s.state.Shares, code)
		s.saveNow()
	}

	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

// freeCode returns an unused code, or "" if the machine cannot produce randomness.
func (s *Server) freeCode() string {
	for attempt := 0; attempt < 12; attempt++ {
		raw := randomHex(codeLength)
		if raw == "" {
			return ""
		}

		var code strings.Builder
		for i := 0; i < codeLength; i++ {
			code.WriteByte(codeAlphabet[int(raw[i])%len(codeAlphabet)])
		}

		if _, taken := s.state.Shares[code.String()]; !taken {
			return code.String()
		}
	}

	return ""
}

// dropExpiredShares removes codes whose week is up. A share is gone from here entirely: there is
// no archive, and the manifest is not kept for any other purpose.
func (s *Server) dropExpiredShares(now time.Time) {
	for code, share := range s.state.Shares {
		if now.After(share.Expires) {
			delete(s.state.Shares, code)
			s.dirty = true
		}
	}
}
