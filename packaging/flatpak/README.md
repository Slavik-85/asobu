# Getting Asobu onto Flathub

Flathub is what Discover (KDE, SteamOS desktop mode), GNOME Software and most Linux app stores
install from. The recipe here, `cc.asobu.Asobu.yml`, repackages the Linux tarball from Asobu's own
GitHub releases. It doesn't compile, because only the release build carries the CurseForge key and
a public Flathub build has no way to be given one.

## Before submitting

1. **Ship a release from v0.1.42 on.** Earlier tarballs have no AppStream metadata inside them, and
   earlier builds don't know they're in a Flatpak. They would keep data outside the sandbox and
   offer updates the store should be doing.
2. **Add screenshots.** Flathub requires them. Put two or three PNG screenshots of the app at
   `docs/assets/screenshots/` (the site serves them from `https://asobu.cc/assets/screenshots/`).
   Add this to `packaging/linux/cc.asobu.Asobu.metainfo.xml` after `</description>`, then cut a
   release:
   ```xml
   <screenshots>
     <screenshot type="default">
       <image>https://asobu.cc/assets/screenshots/library.png</image>
       <caption>Your instances</caption>
     </screenshot>
     <screenshot>
       <image>https://asobu.cc/assets/screenshots/mods.png</image>
       <caption>Mods from Modrinth and CurseForge</caption>
     </screenshot>
   </screenshots>
   ```
3. **Check the test build.** Every successful release runs the **Flatpak** workflow, which can
   also be run by hand from the Actions tab. It builds the recipe the way Flathub does against
   the latest release, then runs Flathub's linter. Download the `Asobu.flatpak` artifact from the
   run and try it on a Linux machine:
   ```bash
   flatpak install --user ./Asobu.flatpak
   ```
   ```bash
   flatpak run cc.asobu.Asobu
   ```

## Submitting

1. Fork https://github.com/flathub/flathub. Untick "copy the master branch only".
2. Clone your fork's `new-pr` branch and branch off it:
   ```bash
   git clone --branch=new-pr https://github.com/<you>/flathub.git
   ```
   ```bash
   cd flathub && git checkout -b add-asobu
   ```
3. Copy `cc.asobu.Asobu.yml` into it. Set the `url` and `sha256` to the latest release's tarball;
   the test workflow prints both at the top of its log. Commit and push.
4. Open a pull request against **`new-pr`** (not `master`), titled `Add cc.asobu.Asobu`. Worth
   saying in the description:
   - Why it repackages the release tarball rather than compiling: the CurseForge API key can't be
     public.
   - That the source is GPL-3.0 at https://github.com/Slavik-85/asobu.
   - Why each permission is there:
     - `x11`: the UI toolkit (Avalonia) draws through X11.
     - `dri`: Minecraft needs the GPU.
     - `pulseaudio`: game sound.
     - `network`: downloads and sign-in.
     - `StatusNotifierWatcher`: the tray icon.
     - `xdg-download:ro`: picking up mods whose author only allows downloads from their own page.
5. Reviewers comment on the PR; `bot, build` in a comment starts a test build. When it's merged,
   Flathub creates `github.com/flathub/cc.asobu.Asobu` and gives you write access to it.

## After it's live

- **Verify the domain**, so the store shows Asobu as verified. Sign in to https://flathub.org with
  GitHub, open the developer portal, choose Asobu, then Verification, and pick the website
  method. Flathub hands you a token. Put it in `docs/.well-known/org.flathub.VerifiedApps.txt`
  in this repo and push. The site then serves it at
  `https://asobu.cc/.well-known/org.flathub.VerifiedApps.txt`, which is where Flathub looks.
- **Updates come by themselves.** Flathub's external data checker reads the `x-checker-data`
  block, notices each new GitHub release, and opens a pull request on
  `flathub/cc.asobu.Asobu` with the new URL and checksum. Merge it and the store updates.
