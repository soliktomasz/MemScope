# Cross-Platform Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Validate MemScope's Release build and full test suite on Windows, macOS, and Linux and document platform limitations.

**Architecture:** A single GitHub Actions matrix job runs the same explicit .NET 10 commands on all three hosted operating systems. Existing real-process acceptance tests provide platform integration coverage; the README records constraints that CI cannot eliminate.

**Tech Stack:** GitHub Actions, .NET 10, xUnit, Markdown

**Spec:** `docs/superpowers/specs/2026-09-01-cross-platform-validation-design.md`

## Global Constraints

- Target Windows, macOS, and Linux.
- Run restore, Release build, and Release tests as separate steps.
- Do not hide platform-specific limitations or suppress failing matrix entries.
- Keep review and implementation overhead minimal.

---

### Task 1: Add cross-platform CI and support documentation

**Files:**
- Create: `.github/workflows/ci.yml`
- Modify: `README.md`

**Interfaces:**
- Consumes: `MemoryProfiler.sln`, .NET 10 SDK, existing xUnit acceptance tests
- Produces: `CI / build-and-test (<os>)` checks for `ubuntu-latest`, `macos-latest`, and `windows-latest`

- [ ] **Step 1: Add the workflow**

Create `.github/workflows/ci.yml` with `contents: read`, push/PR triggers for `main`, `fail-fast: false`, the three-runner matrix, `actions/checkout@v7`, `actions/setup-dotnet@v6` with `10.0.x`, and these steps:

```yaml
- run: dotnet restore MemoryProfiler.sln
- run: dotnet build MemoryProfiler.sln --configuration Release --no-restore
- run: dotnet test MemoryProfiler.sln --configuration Release --no-build
```

- [ ] **Step 2: Document platform support**

Add a `Platform support` README section stating that the core attach, metrics, dump capture/opening, and GC-root workflow is supported on Windows, macOS, and Linux. State same-user permissions, local-only attachment, platform-specific dump formats, same OS/architecture-family analysis expectations, and the need for manual native-picker/UI validation.

- [ ] **Step 3: Validate the workflow and repository**

Parse the YAML with an available local YAML parser, run `git diff --check`, then run:

```bash
dotnet restore MemoryProfiler.sln
dotnet build MemoryProfiler.sln --configuration Release --no-restore
dotnet test MemoryProfiler.sln --configuration Release --no-build
```

Expected: valid YAML, clean diff, successful restore/build/test. GitHub Actions provides final Windows and Linux execution evidence unavailable locally.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml README.md docs/superpowers/plans/2026-09-01-cross-platform-validation.md
git commit -m "ci: validate profiler cross platform"
```
