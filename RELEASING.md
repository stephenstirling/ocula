# Releasing Ocula

Ocula is delivered through the **Microsoft Store**. The Store signs the package with a
certificate Microsoft already holds, so there's nothing to buy, nothing to renew and no
"unknown publisher" warning; it also hosts the download and delivers updates automatically.
GitHub holds the source code and the release notes.

| | Microsoft Store | GitHub |
|---|---|---|
| What it's for | The download people install, and its updates | Source code, issues, release notes |
| Signing | Microsoft signs it (free) | Nothing to sign |
| Cost | Free for individual developers | Free |

## One-time setup

1. **Partner Center account (free).** Go to https://storedeveloper.microsoft.com, choose an
   *Individual* account, sign in with a personal Microsoft account and complete the quick ID
   check. No credit card or fee is needed. The name on the account is the publisher name people
   see ("Stephen Stirling").
2. **Reserve the name "Ocula"** in Partner Center. It tells you straight away if it's taken.
3. In Visual Studio: **Project > Publish > Associate App with the Store** and pick the reserved
   name. This writes the Store identity into `Package.appxmanifest`.
4. `Services/AppInfo.cs` has `GitHubRepository = "stephenstirling/ocula"`, so Ocula can show
   release notes; set `StoreProductId` once the Store gives you one. Update checks use GitHub's
   public API, which can't see a private repository, so make the repository public before the
   first release (or set `GitHubRepository` to `""` to turn update checks off).
5. Publish `PRIVACY.md` (it's in the repository) and keep its web address for the Store form.
   Partner Center can also host a privacy policy for you if you'd rather.

## Each release

1. **Write the release notes** in `packaging/release-notes.md`. People see these in Ocula after
   updating, so write them for photographers, not developers.
2. **Raise the version** in `Package.appxmanifest` (`Identity Version`, e.g. `1.2.0.0`).
   Keep the fourth number `0`: the Store reserves it. Each submission needs a higher version
   than the last.
3. **Build the Store package:** Project > Publish > Create App Packages > Microsoft Store,
   for x64. Run the Windows App Certification Kit when offered.
4. **Submit it** in Partner Center (upload the `.msixupload`, check the listing, submit).
   Certification usually takes from a few hours to a few days.
5. **Tag the release** so GitHub gets the notes:
   ```
   git tag v1.2.0
   git push origin v1.2.0
   ```
   The *Release notes* workflow creates the GitHub release from `packaging/release-notes.md`.
   Use the same three-part number as the package (`v1.2.0` for `1.2.0.0`): Ocula looks the
   notes up by that tag.

## How people get updates

- The Store updates Ocula in the background on its own.
- Ocula also checks once a day (Settings > Updates): if the Store has a newer version, a bar
  offers **Update** and links to "What's new" from the GitHub release.
- The first time a new version runs, Ocula shows its release notes.

## If you ever want downloads outside the Store

Direct downloads from GitHub need a package signed with a certificate Windows trusts, which
costs money. The app already supports it (it detects installs made with an `.appinstaller` file
and offers "Restart and update"), so nothing in the code would change. As of mid-2026, Azure
Artifact Signing (formerly Trusted Signing) costs about US$9.99 a month, signs from GitHub
Actions, and is open to individual developers in the USA and Canada. Commercial certificates
now keep their key on hardware or in the authority's cloud and last at most 458 days, so the
old "certificate file in a GitHub secret" approach doesn't work. You'd also need a second
package identity, because a Store package and a GitHub package can't update each other.
