# Release Process Documentation

MemScope releases are versioned with [MinVer](https://github.com/dotnet/MinVer) and published by the
`Release` GitHub Actions workflow (`.github/workflows/release.yml`). The workflow builds the app for
Windows, Linux, and macOS (Intel + Apple Silicon), packages it with
[Velopack](https://github.com/velopack/velopack), and creates a GitHub release with the generated
Velopack feeds.

## Overview

| Piece | Where | Purpose |
| --- | --- | --- |
| Versioning | `src/MemoryProfiler.App/MemoryProfiler.App.csproj` (MinVer) | Runtime/assembly version derived from git tags prefixed with `v` |
| Release pipeline | `.github/workflows/release.yml` | Builds, packages, and publishes on `v*` tag pushes or `workflow_dispatch` |
| Release helper | `release_script.sh` | Bumps the README version badge, commits, and creates/pushes the tag |
| Process doc | `RELEASE.md` | This file |

## Versioning (MinVer)

- Tags look like `v0.1.0`, `v0.2.0-rc.1`, etc. (`MinVerTagPrefix` is `v`).
- The minimum baseline is `0.1` (`MinVerMinimumMajorMinor`), so **untagged** commits build as
  `0.1.x-preview.<height>` (e.g. `0.1.0-preview.0.25`) and are clearly pre-release.
- Building/publishing a commit **with** a tag such as `v0.2.0` produces version `0.2.0`.
- The runtime version comes from MinVer, so always tag the exact commit you want to release.

## Release Flow (recommended)

Run the helper from the repository root, from `main`:

```bash
./release_script.sh          # interactive
./release_script.sh 0.2.0    # or provide the version directly
```

The script:

1. Shows the current git tag and README badge version.
2. Validates the requested version (`MAJOR.MINOR.PATCH` or `MAJOR.MINOR.PATCH-PRERELEASE`).
3. Checks that you are on `main`, that there are no uncommitted changes, and that the tag does not exist.
4. Bumps the version badge in `README.md`.
5. Commits the bump, creates the annotated tag `v<version>`, and pushes it.

Pushing the tag triggers the `Release` workflow automatically.

### Manual flow (no script)

```bash
dotnet build MemoryProfiler.sln          # sanity check
git tag -a v0.2.0 -m "Release 0.2.0"
git push origin main
git push origin v0.2.0
```

### Manual flow from GitHub UI (fallback)

Run the **Release** workflow with **Run workflow** and enter the version (e.g. `0.2.0`). The
workflow builds with that version and, at the end, creates and pushes the `v0.2.0` tag itself so
the release stays reproducible. Pushing that tag re-triggers the workflow once more — the
follow-up run rebuilds the same version and republishes the same release/tag, so it is redundant;
you can cancel it after it starts or simply ignore it. Releases cut via `release_script.sh` never
see this second run.

## What the workflow does

1. **Version extraction** — from the `MinVer: Calculated version` build output (tag-triggered runs)
   or from the `workflow_dispatch` version input.
2. **Publish** — self-contained, single-file, compressed publish for `linux-x64`, `win-x64`,
   `osx-x64`, and `osx-arm64`.
3. **Velopack pack** — creates the packages/feeds per OS channel (`linux-x64`, `win-x64`,
   `osx-x64`, `osx-arm64`).
4. **Create GitHub release** — via `softprops/action-gh-release`, with auto-generated release notes.
5. **Velopack upload** — uploads each channel's assets and `assets.<channel>.json` feed to the
   release, so a future in-app updater can consume them (retries transient failures up to 3 times).

### Code signing (macOS) and secrets

Signing is **optional and secret-gated**: if the secrets below are set on the repository, macOS
packages are Developer ID-signed and notarized; if they are absent the workflow runs unsigned
(same for the unsigned Windows installer). Until secrets are configured:

- macOS builds are not notarized — Gatekeeper will warn users (right-click → Open to bypass).
- Windows installer is unsigned — SmartScreen will warn users.

| Secret | Purpose |
| --- | --- |
| `BUILD_CERTIFICATE_BASE64` | Base64 of the "Developer ID Application" `.p12` |
| `INSTALLER_CERTIFICATE_BASE64` | Base64 of the "Developer ID Installer" `.p12` |
| `P12_PASSWORD` | Password of both `.p12` files |
| `APPLE_ID` / `APPLE_PASSWORD` / `APPLE_TEAM` | Apple ID used with `notarytool` |
| `KEYCHAIN_PASSWORD` | Password for the throwaway signing keychain |

The signing identity names in the macOS pack step default to `Developer ID Application/Installer:
Tomasz Solik`; adjust them if the certificates belong to a different team/member.

## Version format

- **Stable releases**: `MAJOR.MINOR.PATCH` — `0.1.0`, `0.2.0`, `1.0.0`
- **Pre-releases**: `MAJOR.MINOR.PATCH-PRERELEASE` — `0.2.0-rc.1`, `1.0.0-beta.1`

Versions containing `preview`, `alpha`, `beta`, or `rc` are published as **pre-release** GitHub
releases.

## Best practices

1. **Before releasing**:
   - All changes are merged to `main` and committed.
   - `dotnet build MemoryProfiler.sln` and `dotnet test MemoryProfiler.sln` pass locally (the
     acceptance tests spawn real target processes).
   - Pick the version per semver: PATCH for fixes, MINOR for features, MAJOR for breaking changes.
2. **After pushing the tag**:
   - Watch the run at <https://github.com/soliktomasz/MemScope/actions>.
   - Verify the GitHub release exists with per-platform Velopack assets and feeds.
   - Smoke-test downloaded artifacts on each OS; unsigned macOS/Windows builds need the
     Gatekeeper/SmartScreen bypass.

## Troubleshooting

### "You have uncommitted changes"
Commit or stash first, then re-run:
```bash
git stash && ./release_script.sh 0.2.0 && git stash pop
```

### "Tag v0.2.0 already exists"
```bash
git tag -d v0.2.0                                  # local
git push origin :refs/tags/v0.2.0                  # remote (only if it must be re-done)
```

### Workflow fails at the signing step
Usually missing/mismatched secrets. Either configure the secrets, or release unsigned — the
signing steps are skipped automatically when `BUILD_CERTIFICATE_BASE64` is empty.

### Version looks wrong (e.g. `0.1.0-preview.0.N`)
The tag is missing or does not point at the released commit. Confirm with
`git describe --tags --abbrev=0` and tag the correct commit.
