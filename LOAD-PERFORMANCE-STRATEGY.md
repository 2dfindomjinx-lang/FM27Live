# Windows continuation — 15 November 2026

The deferred Transfer Deals work is implemented and measured on Windows Steam `27.0.1+2503222`. The overall performance goal remains incomplete: three complete UI loads took **8.569 / 10.093 / 7.723 seconds**, with zero stage errors. One run exceeded ten seconds and none reached three seconds. Keep PR #2 draft pending the remaining verification and performance work.

## Current implementation

- `LoadedGameData` retains a session-owned deal index with attempted, loaded and error state. Empty success is retained. Failed discovery is reported as incomplete loading and cannot retry implicitly on tab access.
- `PlayerProposalsView` uses non-null retained candidates, profile-derived person offsets and session/generation checks. The static cache, sixty-second expiry, first-tab sweep, automatic empty-result proposal refresh and invalidation after fee/status writes are removed. Deal records and write guards remain live. **Load Game is the explicit refresh for newly created/deleted deals.**
- `HeapVtableScanner` discovers deals and agreement regions together, preserving enumeration order, all three previously accepted deal vtables, vector tails and ordinary unreadable-range failures. A failed wide read retries bounded pages; a failed page produces an error rather than partial success.
- Windows region enumeration explicitly marks OS guard pages. Discovery excludes these access traps rather than treating them as readable object storage. This was checked against VirtualQueryEx (`PAGE_READWRITE | PAGE_GUARD`) for the failed range in the initial strict scan. Other region consumers retain their existing behavior.
- Providers explicitly implementing `IConcurrentMemoryReads` use at most four dedicated workers, each holding a 4 MiB buffer. Other providers are serial. The raw provider handles discovery; scoped bulk-read caches are never shared across workers.
- The shared heap pass overlaps catalogue materialization and is joined before publishing loaded data. Errors remain captured in the same load report; agreement-region preload failures are retained as well. No work is moved behind the ready marker and no forced GC was added.

## Executed evidence

- Original complete deal scan: **36.870 seconds**, 7,371 candidates; all 7,371 live reads succeeded. Writable region inventory was approximately 3.92 GB.
- Allocation-free sequential discovery: approximately 16.3 seconds; approximately 16.0 seconds were process reads.
- Bounded parallel deal discovery: **4.019 seconds**, exact candidate-address sequence/hash equality against the original capture.
- Final shared-discovery algorithm: **4.481 seconds** in the isolated read-only harness; all 7,371 deal candidates and all 28 agreement-region records (addresses, IDs and names) exactly matched independent pre-change captures.
- End-to-end UI loads, including fresh reconnect, all catalogues, shared heap discovery, binding, hooks/freezer initialization and cleanup: **8.569 / 10.093 / 7.723 seconds**. All three reported zero errors and stable dataset counts. These are release Windows app measurements, not Mac measurements or an isolated scan benchmark.
- Stable counts: 50,038 players, 23,536 staff, 5,239 clubs, 231 nations, 8,215 competitions, 29,460 stadiums, 58,475 cities, 263 awards, 2,078 referees, 136 injury types, 104 languages, 73,574 person names, 13,560 career entries, 9 retirements, 194 future transfers, 7,371 deals and 28 agreement regions.
- Windows release suite: **1,309 Core + 297 App tests**, zero skips/failures. Added coverage includes empty/failed/uninitialized indexes, reconnect isolation, live fee/status and stale-vtable reads, compatibility types on Steam/Game Pass fixtures, chunk tails, multiple target types, original region order, wide-read fallback, unreadable ranges, guard-region exclusion and agreement-region preload errors.
- In a separate isolated copy, dropping compatibility vtables made seven scan tests fail; removing the attempted-index guard made both empty/failed lifecycle tests fail. Production sources were not mutated. No tests were removed, skipped or weakened.
- Observed process working set settled near **453 MiB**, with a process peak near **467 MiB**. A five-second post-load sample used **1.375 CPU seconds** (27.5% of one core); this does **not** establish zero idle CPU. A separate 15.24-second reload-plus-idle sample used 25.47 CPU seconds. Do not compare these Windows measurements directly with the older Mac RSS/CPU figures.
- Investigation of the existing manager prefix found that its primary offer vector omitted two baseline candidates. Another vector contained those two, but that observation does not prove a complete authoritative source across builds. No guessed manager offsets or new JSON offsets were introduced.

## Remaining work

1. Reduce full-load variability and the remaining heap-read cost; the strict ten-second ceiling and three-second aspiration are not established.
2. Investigate sustained Windows post-load CPU with profiling, distinguishing UI rendering and existing polling from discovery. Do not force GC or hide the loading indicator early.
3. Run live Game Pass validation and repeat final Mac native-package measurements. This machine supplied only a running Steam game. Profile fixtures and Windows tests do not replace live evidence for other builds/platforms.
4. Preserve the live UI restriction (Load Game and Wonderkid popup only). No broader tab navigation or game-edit actions were used here. First-tab lifecycle behavior was covered with fake-memory tests and source audit; mutation planning and explicit refresh paths still intentionally read live data.
5. Keep raw captures, candidate addresses, probe projects, mutation copies and timing logs local. They are under the continuation worktree's ignored `re/load-perf/` directory.

The sections below are the historical Mac checkpoint and its original continuation strategy. Their statements that the deferred deal scan remains unimplemented or Windows timing is unavailable are superseded by this section. Their platform caveats and evidence provenance still apply.

---
# Load performance strategy and handoff

## Status: reviewable checkpoint, original goal still incomplete

This is the continuation guide for another model. The user's latest instruction is to preserve the implemented work, create a PR, and document the next optimization strategy.

The non-negotiable goal is approximately 3 seconds, at most 5–10 seconds for a complete load. Do not make the loading overlay disappear while expensive shared indexes still load on first tab access. Preserve every record and all live write guards.

The implementation checkpoint is e9b7733, based on upstream main 6234cc2. The PR branch is codex/complete-load-performance. It includes portable performance changes, full loading coordination, memory lifetime fixes, the hidden loading-animation CPU fix, and the Wonderkid Academy on/off button.

**A late audit found a remaining deferred Transfer Deals heap scan. The measured 3.6–4.2 second runs exclude that first-use scan. The original no-slow-tabs requirement is therefore not achieved yet. Do not describe this checkpoint as finished.**

## Start here on another computer

This handoff is portable. The absolute paths below identify the original workstation only; they are not required paths and must not be copied literally into commands on another computer.

1. Clone the public repository and check out the PR branch:

   ```bash
   git clone https://github.com/Jackhamerp24/Football-Manager-27-Editor.git
   cd Football-Manager-27-Editor
   git fetch origin
   git switch --track origin/codex/complete-load-performance
   git status --short
   git log -3 --oneline
   ```

   If the branch already exists locally, use `git switch codex/complete-load-performance` and update it without overwriting local changes. If PR #2 has already merged, start from current `main` and re-audit the remaining tasks; do not resurrect obsolete code from this document.

2. Read `AGENTS.md`, this file and the current PR description/diff. Treat current source and CI results as authoritative; phase statuses and measurements in this handoff are historical evidence, not a guarantee about a newer commit.
3. Use Windows 10/11 with the .NET 10 SDK for the portable upstream app and its normal desktop/native tests. Run:

   ```bash
   dotnet build Fm27Editor.slnx -c Release
   dotnet test Fm27Editor.slnx -c Release
   ```

   Core fake-memory tests need no game. Live measurements require a supported Football Manager build with a save loaded. Keep the user's restriction: interact only with Load Game and the Wonderkid Academy popup unless the user changes that restriction.
4. No local logs, captures, absolute memory addresses, credentials or access to this original computer are required to continue coding. The ignored `artifacts/` and `.slim/` evidence folders were NOT pushed. If a claim needs stronger evidence, reproduce the baseline from the checked-out source on the new computer and record fresh counts, hashes, errors and timings. Never reuse memory addresses from another process/save/session.
5. This public PR contains the Windows/shared implementation. It does NOT contain the full macOS port or Mac helper. A Mac agent can inspect shared source and use a suitable isolated test host, but it must not assume the upstream checkout can run the measured Mac app. Continuing Mac-specific runtime work requires a separately supplied complete Mac checkout/patch, including its existing profiles, helper and packaging scripts. Do not invent missing platform code or copy only selected Mac files and call that a complete port.
6. Creating/updating the existing PR requires the new machine's own GitHub authentication and repository write permission. Read-only code review and local implementation do not require the original author's credentials. Keep PR #2 as the shared continuation point; check for newer commits before pushing and do not merge without the user's instruction.

The next agent should first state which environment it actually has (Windows live game, Mac port, or code/test-only), reproduce the applicable checks, and then take the next unfinished item below. Missing original captures are an evidence gap to reproduce, not a reason to mark the goal complete or block ordinary source work.

## Checkouts and artifacts

- macOS working checkout: /Users/duong/Desktop/FM27-mac-kit, branch mac-swift. This had extensive pre-existing staged, unstaged and untracked Mac-port changes. Preserve their contents and staging; never reset or copy this checkout wholesale into upstream.
- Portable upstream checkout: /Users/duong/Desktop/FM27-mac-kit/.slim/worktrees/upstream-main, now branch codex/complete-load-performance. Its base was pulled from origin/main with --ff-only.
- The macOS app bundle is dist/Fm27Editor.app under the Mac checkout.
- Windows x64 self-contained output: artifacts/load-performance/main-win-x64 under the Mac checkout.
- Local task state: .slim/deepwork/load-performance.md, app-load-plan.md and core-scans.md under the Mac checkout.
- Original source/diffs/app baseline: artifacts/load-performance/baseline and baseline.app. Comparison against this snapshot isolates this task from pre-existing changes.
- Local logs, hashes, raw captures and benchmark harnesses: artifacts/load-performance/. They are ignored and must remain local.
- HANDOVER.md in the Mac checkout describes the earlier investigation and this checkpoint.

Only the portable changes are in this PR. Mac memory/helper/ARM64/platform profiles and packaging are not imported into upstream.

## What was implemented

### Reading and discovery

BulkReadScope supplies read-only, bounded 4 KiB page read-ahead during bulk repository reads. Its default page budget is 16 MiB. Scope disposal drops raw page references. Nested scopes share the outer cache without owning it. Scopes are thread-confined. Unreadable speculative pages fall back to the exact requested range.

PlayerReader caches name-entry pointers for one reader lifetime and hoists constant enum sizes. All bulk entity readers use the scoped reader; normal single-record Reload and writers retain original memory.

The Mac stadium reader avoids walking the same contiguous stadium run repeatedly. That Mac-only algorithm is absent from the upstream Windows reader and was not copied into the PR.

The region catalogue uses existing profile indirection on Mac and preserves a heap fallback. Portable heap matching avoids per-word LINQ work and handles candidates at chunk tails.

Club offers use the existing validated transfer-manager offer vector when available, validating slot IDs and existing offer predicates. Invalid manager data retains the old fallback. Explicit refresh and mutation reads stay live.

### Work permits: the main breakthrough

The old work-permit discovery scanned approximately 3.5 GB of heap and made millions of tiny memory reads. It took 70–110 seconds in isolated baseline runs and accounted for prolonged CPU usage in native loading.

Read-only investigation found the same table inside the transfer manager already identified by profile ManagerRva and ManagerVtable. The production implementation does NOT hard-code the observed permit-vector offset.

The search bounds the manager prefix using the already measured offer-serial field. On Mac this comes from ProposalOffsets.OfferSerial; upstream reuses FutureTransferCreator.OfferSerial, an existing Windows constant whose visibility alone changed. It enumerates structurally plausible vectors inside that prefix and requires exactly one completely valid table.

Every pointed 32-byte record is checked, including nation identity and nondecreasing person indexes. Candidate validity is three-state:

- Any independently invalid record disproves a candidate.
- Missing data leaves it unknown unless another record disproves it.
- Only every record readable and valid establishes a valid candidate.

Unknown or ambiguous results fall back to the old heap scan. Before publishing the located table, the root pointer, manager vtable, selected vector header and raw sample checks are repeated without cached pages.

Full validation of the measured 983 records and pointer order matched the old scan byte for byte. The final fast lookup measured 9.383 ms in the harness and 6–7 ms in the app.

Old builds without manager metadata keep the fallback. Do not claim the Mac timing holds for every older profile.

### macOS helper work, not included in the portable PR

MacHelperProcessMemory now supports bounded native scatter reads using opcode 13. Each item reads 1–32 bytes; requests contain at most 1024 items. The C helper validates the complete request before reading memory and returns one explicit result per item. The client validates a complete response before copying successful data.

Only an explicit legacy unknown-opcode response triggers one-time fallback. Malformed or interrupted frames close the connection. Existing read/write framing and permissions are retained.

The helper update was installed through the app's existing MacElevation workflow. Installed and bundled helper hashes matched. Actual C self-task/socketpair tests cover the decoder without game access.

### Load coordination and memory lifetime

LoadedGameData owns retained records for one exact session. GameLoading and MainWindow hold the loading barrier through:

- entity lists and all registered screen row preparation/filtering;
- clubs, nations, competitions, staff, stadiums, awards, referees, cities and injury types;
- city-link, language, person-name, career and agreement-organisation catalogues;
- retirement and future-transfer lists;
- applicable proposal/clause/region/youth/work-permit initialization;
- hook-site lookup, freezer restoration and existing reload cleanup.

Players load independently of selected navigation. Empty-loaded lists are distinct from uninitialized lists. Old asynchronous screen results cannot publish into a new generation. Club metadata travels with its own returned row collection.

Search catalogues no longer expire after 60 seconds. Reload cancels old searches and releases search indexes, custom-grid rows, result closures and old screen data. SectionHost releases controls owned by that window while preserving selected-section preferences; window events unsubscribe on close. Owned timers stop, and the hidden loading bar no longer animates.

No manual GC was added. Record objects are shared by lists/search/screens; reference arrays do not duplicate the full dataset.

### Wonderkid Academy

The portable PR includes Turn on / Turn off controls and localization keys. It uses existing Configure, SwitchOff, Read and Undo behavior. SwitchOff retains configuration. Existing core tests cover enabled/disabled state and retained settings.

Live UI testing was restricted by the user to Load Game and dismissing/disabling the Wonderkid popup. No other tabs or game-edit controls were exercised.

## Measured evidence and limits

Native Mac runs on the currently loaded Steam 27.0.1+2503222-mac save:

| Run | Recorded ready time | Stage errors |
| ---: | ---: | ---: |
| 1 | 4.202 s | 0 |
| 2 | 3.708 s | 0 |
| 3 | 3.822 s | 0 |
| 4 | 3.632 s | 0 |

These are the implemented barrier's timings, NOT proof of all first-tab work: Transfer Deals remains outside it.

Each run's counts were checked: 49,051 players; 24,705 staff; 5,090 clubs; 251 nations; 8,216 competitions; 29,461 stadiums; 58,475 cities; 263 awards; 2,078 referees; 136 injury types; 104 languages; 73,744 person names; 13,431 career entries; 6 retirements; 0 future transfers. Ten original datasets matched their serialized baseline hashes. All 983 permit records and pointer order matched separate baseline captures.

Final run logs: final-native-loads.log and final-native-results.json in the local artifacts folder. Work-permit proof: wp-fast-root-final.log plus local pointer/record captures. Do not publish raw game-derived material.

Settled idle observations were about 178 MiB RSS and 0.0% CPU. Reload peak was about 434 MiB. Brief active-load CPU can approach one core; the previous prolonged work-permit scan was removed on the measured build. RSS fell normally without forced GC.

The final bounded SectionHost cleanup happened after these four timings. Its full test suite passed and the final Mac bundle was rebuilt; the rebuilt bundle has not had a new final live timing. Recheck it when resuming.

### Tests and builds

- Final Mac source: 1,428 Core + 294 App tests passed.
- Native C helper self-memory protocol tests passed; clang -O2 -Wall -Wextra compiled without warnings.
- NativeAOT packaging and code-sign checks passed, with existing Avalonia reflection/trimming warnings.
- Upstream baseline: 1,259 Core tests total, 37 failures on Mac.
- Modified upstream: 1,296 Core tests total, 37 failures. Failure names and first messages exactly matched pristine upstream: 36 Windows kernel32-dependent cases and one Windows path-separator assertion. All 37 added tests passed.
- All 293 upstream App tests passed in an isolated copy using the proven headless test host. Production and test method sources were unchanged. The normal upstream App test host fails AppKit initialization on Mac; no test was skipped or weakened.
- Windows x64 self-contained publish succeeded.
- No upstream offset JSON changed. Steam and Game Pass profile fixtures passed.
- After PR creation, GitHub Actions ran the actual Windows test suite successfully: 1,296 Core + 293 App tests, zero failures or skips, on Windows Server 2025. Run: https://github.com/Jackhamerp24/Football-Manager-27-Editor/actions/runs/43687799782 . This supersedes the local platform-failure uncertainty above; the local comparison remains useful diagnostic history.
- Live Windows Game Pass/Steam timing was not available and must not be claimed.

## Priority 1: finish Transfer Deals before claiming the original goal

PlayerProposalsView.ProposalsPanel still invokes session.TransferDeals.Scan after initially drawing proposals when it cannot find a live deal. HasTransferProposals uses the same static cache with a 60-second expiry. This is separate from session.Proposals.EnsureLoaded.

TransferDealReader.Scan in Core/Clubs/TransferDeal.cs loops through writable heap regions and checks vtables at each 8-byte position. VtableRvas.Any currently creates repeated iterator/delegate work inside that loop. Its total time has NOT been measured in this task.

The stable checkpoint intentionally retains this behavior because the user requested a handoff before an unmeasured new phase. No attempted fix has been left half-applied.

### First obtain decisive evidence

1. Close only the editor test instance before attaching a separate read-only harness: the helper serves one client connection at a time.
2. Measure the current complete deal-candidate scan, region bytes, candidate count, read failures and candidate-address hash.
3. Validate representative candidates with the existing live Read method. Capture the old complete candidate set as the equivalence baseline.
4. Audit Proposals.Refresh triggered by an empty filtered result despite HasOfferIds. On manager-vector builds this is cheap, but legacy profiles can scan again.
5. Inspect measured transfer-manager vectors as a possible complete authoritative source. Do not assume offers and deals are the same collection, infer completeness from count alone, or guess new offsets.

### Then change lifecycle coherently

Add a per-session deal index to LoadedGameData, including an explicit attempted/loaded/error state and IReadOnlyList<ulong> candidates.

Populate it once within the measured load barrier. Retain valid empty results. If the scan fails, report incomplete loading and display the error; do not silently retry on tab opening.

Remove the static candidate cache, lock and 60-second expiry from PlayerProposalsView. Both ProposalsPanel and HasTransferProposals must pass the retained NON-null candidate list to FindForPlayer. Null currently triggers a scan.

Keep each deal's Read live, so fee/status changes and stale objects are revalidated. Do not invalidate the whole candidate index after ordinary fee/status writes; re-read affected records. New/deleted deals require an explicit refresh strategy tied to session/game progress, not an invisible first-tab heap sweep.

Use profile-derived person addresses. Mac already has PersonAddressOf; upstream still has pre-existing +0x288 paths to audit carefully.

Do not wrap the raw region-enumeration provider in BulkReadScope and then expect IMemoryRegions to survive. Use the raw provider for discovery and bounded read-only scopes for record materialization.

### Optimize only with equivalence evidence

First hoist vtable values into a fixed set outside the scan loop. Preserve ALL currently recognized types, including the two pre-existing compatibility RVAs, until independent evidence justifies changing them. Then use allocation-free comparisons or vectorized negative filtering with correct chunk tails and original source order.

A validated complete manager-owned source is preferable to repeated whole-heap scans, but needs the same kind of ownership, full-record and ambiguity proof as work permits. Missing/unreadable/ambiguous data must not become a fabricated empty success.

Never meet the time target by omitting this index or moving it back after ready.

### Required tests

- Empty successful index does not rescan on first tab access.
- Failed index stays an explicit load failure and does not auto-rescan.
- Old-session data is rejected after reconnect.
- Fee/status changes are read live from retained candidate addresses.
- Candidate set/hash is equivalent before and after scan optimization.
- Chunk boundaries, unreadable ranges and multiple vtable variants are covered with hand-built fixtures.
- Full load timing includes the deal phase and its failures/counts.

## Priority 2: close the remaining verification gap

After Transfer Deals is included, rerun the final native package at least three times, with each reload reading fresh data. Time from Load Game to the real ready marker, including cleanup and UI binding. Inspect phase errors and counts.

Keep the user's UI restriction: only Load Game and the Wonderkid popup. Use fake-memory/lifecycle tests for navigation; ask for broader live interaction only if indispensable.

Verify Windows builds and profile parity. Run Windows-native tests and live read-only measurements when that environment exists. Clearly separate Mac measurements from portable compilation and synthetic Windows evidence.

Audit every remaining shared Catalogue/LoadAll/Scan/EnsureLoaded call reachable from first tab access. Mutation planning and explicit refresh are allowed fresh reads; shared full-database initialization must be complete before ready.

Only then claim the original goal is achieved. Aim for 3 seconds; do not describe 3.6–4.2 seconds as exactly 3 seconds.

## Approaches tried and lessons

- Merely caching string payloads was insufficient; entry pointers and shared read pages reduced tiny IPC traffic.
- Stadium repeated-anchor scans were an algorithmic problem, not a reason to defer stadium data.
- Work-permit candidate batching and native scatter reduced transport cost but could not remove the full heap scan. Finding the validated owner was the decisive improvement.
- A proposed “complete writable map” filter was removed: existing Mac enumeration can silently truncate on query errors, so it cannot prove absence. Do not restore that unsound capability or discard candidates using an incomplete map.
- Failed speculative page hints may bypass another wide probe, but must always retry precise reads. Never cache unreadable data as a permanent negative result.
- Native batch protocol is bounded, fully parsed and backward-compatible. Do not remove framing/error tests while simplifying it.
- Do not force GC based on peak RSS. Fix actual strong references and distinguish peak allocation from settled memory.
- Never compare a direct MacProcessMemory harness with a helper-based app timing. The runner sometimes gained direct access; later harnesses explicitly forced MacHelperProcessMemory.
- Do not benchmark concurrently with builds/tests or another helper client. Machine load caused large timing variation.
- Keep progress logs and raw captures ignored. The public PR should contain code, independent tests and this strategy, not local memory addresses.

## Completion checklist for the next model

- [ ] Transfer Deals index is session-bound and included before ready.
- [ ] No expensive hidden scan remains in first-tab paths.
- [ ] Full records/candidate sets match independent baselines.
- [ ] Three final native loads remain under 5–10 seconds, with the 3-second target honestly reported.
- [ ] No stage error is hidden as empty success.
- [ ] CPU/RAM evidence separates active peaks from idle behavior.
- [ ] All relevant tests pass or have an exact pristine-baseline explanation.
- [ ] Windows live verification limits are explicit.
- [ ] Draft PR is updated with final scope and validation before marking ready.
- [ ] No merge or branch-protection change occurs without the user's requested action.

The original performance goal remains active/incomplete at this handoff. The user's screenshot showed that GitHub main has no branch protection; the request was to create a PR. No branch-protection setting has been changed.

## Delivery

Draft PR #2: https://github.com/Jackhamerp24/Football-Manager-27-Editor/pull/2 . It targets main and explicitly documents the remaining Transfer Deals work. The original performance goal is not marked complete.
