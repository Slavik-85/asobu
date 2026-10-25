# Asobu's Flatpak

Asobu runs its own Flatpak repository at `https://asobu.cc/flatpak/repo/`. Users install it with
one click from `https://asobu.cc/flatpak/asobu.flatpakref`, which GNOME Software and Discover
(KDE, SteamOS desktop mode) open on their own. The same works from a terminal:

```bash
flatpak install --user https://asobu.cc/flatpak/asobu.flatpakref
```

The runtime comes from Flathub, which the `.flatpakref` adds if it's missing. Updates then arrive
through the app store like any other Flatpak.

## How it's built

- `cc.asobu.Asobu.yml` repackages the Linux tarball from each GitHub release. It doesn't compile,
  because only the release build carries the CurseForge key.
- The **Release** workflow's `flatpak` job points the recipe at the new tarball, builds it, signs
  it, and attaches the repository to the release as `asobu-flatpak-repo.tar.gz`.
- The **Website** workflow publishes `docs/` to asobu.cc. It unpacks the newest repository into
  `/flatpak/repo/` and fills the public key into `asobu.flatpakref` and `asobu.flatpakrepo`.

## One-time setup

1. **Settings → Pages → Build and deployment → Source: GitHub Actions.** The site stops coming
   straight from the `docs` folder and is deployed by the Website workflow instead. The custom
   domain stays as it is.
2. **Settings → Secrets and variables → Actions → New repository secret**, named
   `FLATPAK_GPG_PRIVATE_KEY`, holding the whole private key file, header lines included. The key
   has no passphrase, because CI can't type one. Until it exists, releases skip the Flatpak with a
   warning and carry on.

`asobu-flatpak.gpg` is the matching public key (`0B54 CCBF AF69 E5A0 3367 9D21 CE98 E897
8749 51BA`). Keep a backup of the private key somewhere safe outside this repository. If it's
lost, a new key means everyone who installed the Flatpak has to remove it and install it again.
Never commit the private key.

## Flathub: don't submit this recipe

Flathub's [requirements](https://docs.flathub.org/docs/for-app-authors/requirements) forbid
AI-generated or AI-assisted content in a submission's manifest, and disclosing it doesn't make it
acceptable. AI tools may not open, write or automate the submission pull request, its
description, or replies to reviewers. The PR checklist makes the submitter confirm that, and
misrepresenting it can get an account banned for good.

This recipe and most of Asobu were written with AI help, so:

- **Don't submit `cc.asobu.Asobu.yml` to Flathub,** or anything derived from it.
- A Flathub submission has to be a manifest the developer writes themselves, with no AI help.
  The PR also has to be opened and handled by hand, and it must disclose that the app's code is
  AI-assisted. Reviewers may still reject it for that.
- Flathub also generally turns down very young projects, so waiting a while helps either way.

The self-hosted repository above gives the same one-click install and store updates without any
of that.
