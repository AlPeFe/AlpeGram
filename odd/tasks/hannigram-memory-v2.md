# HanniGram memory v2 — hardening + memory scopes + MCP

Feature branch: `feat/memory-v2` (from `main` @ `373cc7d`)
Engram mirror: `odd/hannigram-memory-v2/tasks` (project `hannipi`)

## Objective

Fix the verified defects in the memory engine, add the **global (cross-project) memory
scope** the product actually needs, add a cheap review/lifecycle loop so the model can
save, review, and delete without token waste, and expose the whole thing over MCP.

Deliver a memory that is **simplified, per-project plus global**, not a clone of engram.

## Problem

The engine works but has never been tested. Audit (obs #238) verified six defects, two of
which make memory either unreachable or unretrievable:

1. `GET /api/search?q=ab` returns HTTP 500 — `FtsQuery` drops terms shorter than 3 chars and
   `string.Join` over an empty list yields `""` → `MATCH ''` → `fts5: syntax error near ""`.
   Reproduced end to end on the live daemon and confirmed via the CLI error message.
2. `what` / `why` / `where` / `learned` are not in the FTS index (`fts5(title, content,
   topic_key, type)`), so the richest part of a saved memory can never be retrieved.
3. Recency ignores `updated_at_utc`; topic-key upsert preserves `created_at_utc`, so memory
   that evolves gets buried at its original position.
4. `limit` is unvalidated; `-1` means unlimited in SQLite → full memory dump from an
   unauthenticated local API.
5. `RebuildFts()` re-indexes the whole corpus on every write, under the global lock, with no
   transaction.
6. `sessions` has no uniqueness; `mem_session_summary` duplicates rows and overwrites its own
   summaries.

Structurally, memory is **per-project only**. The product need is per-project **plus** a
global heuristic layer: "how I structure a console app", "my commit conventions", "my
preferred layering" are not project facts — they repeat across every project.

## Why

- Unretrievable memory is worse than no memory: it costs tokens to write and returns nothing.
- Untested SQLite/FTS5 behavior is the shared root cause of defects 2, 3, 5, 6.
- Without a global scope, the same heuristic gets re-taught once per project.
- Without lifecycle, memory only grows; there is no way to tell current truth from stale truth.
- Without MCP the memory is locked to one harness. MCP makes it portable while the harness
  stays the primary, zero-overhead path.

## Scope

In: bug fixes, FTS5 trigger-based indexing, test project + CI, global scope + promotion,
cross-scope search, lifecycle/review, session searchability, JSON export/import, stdio MCP
server, Pi extension tool updates, HanniPI integration, docs.

Out: embeddings/semantic vector search, multi-user auth, remote/server deployment, replacing
the HTTP daemon.

## Constraints

- **Token discipline (hard requirement).** No memory text is injected wholesale into context.
  Review/dedup detection is deterministic and server-side. Every tool response is bounded:
  previews are truncated, counts are capped, and no tool returns the full corpus.
- Deterministic behavior only. No LLM calls inside the store or the service.
- `observations_fts` is **external-content**: synchronization must use the FTS5 `'delete'`
  command with the old column values, or triggers. Never plain INSERT/DELETE against it.
- Existing data must migrate in place. `SchemaVersion` bump triggers a one-off re-index.
- The daemon stays loopback-only by default.

## Memory model (product concept)

| Scope | Granularity | Example | Resolution |
|---|---|---|---|
| `project` | one namespace per repo | "this repo's auth flow", "this ADR" | git remote `owner/repo` |
| `global` | one namespace, all projects | "console app layout", "commit style", "layer conventions" | reserved sentinel project |

Both scopes share one store, one FTS index, one upsert-by-`topic_key` mechanism. A memory is
written to a scope explicitly; it can be promoted from `project` to `global` when it proves
generic. Cross-scope search returns project hits **and** global hits with their origin marked.

Implementation notes (verified in Slice 2): the global namespace is the reserved sentinel project
`_global`, stored in the same `projects` table — no second store. Reads resolve it read-only and
never create it; only a write to `scope=global` materializes the row. `ProjectResolver.Resolve`
never returns the sentinel, so a directory named `_global` stays an ordinary project. Promoting a
memory whose `topic_key` already exists in the global scope overwrites the global row in place
(latest teaching wins) instead of accumulating duplicates.

## HanniPI integration constraints (verified 2026-10-15)

The harness side was mapped before designing the global scope and MCP. These are facts, not
assumptions, and they amend the tasks below.

**How HanniGram is delivered to the harness.** `hannigram-version.json` (keys: `repo`, `tag`,
`sha256`, `commit`, `builtAt`) is read by `scripts/pack-hannigram.ps1` and the release skill.
`hanni-setup.ps1` downloads the asset, verifies its SHA256, installs it to
`%LOCALAPPDATA%\HanniPI\hannigram`, sets `HANNIGRAM_DAEMON`, and copies the asset's
`pi-extension/` to `~/.pi/agent/extensions/hannigram`. So the extension ships **inside the
release asset** — a new tool is only live after a new asset and a version bump.

**Token cost is the binding constraint.** The `sodd` extension hooks `pi.on("context")`, which
fires **before every LLM call**, and injects: the sODD guidance block, up to 12 ADR rows
(`[HanniGram · decisiones/convenciones — respeta esto]`), and up to 6 recent non-ADR rows
(`[HanniGram · contexto reciente]`). It reads SQLite directly and never injects full content.

→ **Plan amendment:** global memory must NOT enter that same injection path wholesale. It gets
its own minimal budget (pinned heuristics only, hard cap) and everything else stays on-demand.
Added as **T21**.

**MCP is natively supported.** Config lives in `<agentDir>/mcp.json` and `<cwd>/.pi/mcp.json`
(`{"mcpServers":{"name":{"command","args","env","cwd"}}}`), stdio or streamable HTTP; tools
register as `mcp__<server>__<tool>` with default exposure `codemode`.

→ **Plan amendment to T16/T17:** the MCP server must be **opt-in and disconnected by default**,
absent from ordinary prompt context, consistent with the previously recorded preference that
MCPs stay optional and loaded on demand. It is for portability to other harnesses; the Pi
extension stays the primary zero-config path.

**Gaps found in the harness contract.**

- `mem_update` is referenced by `.pi/skills/sodd/SKILL.md` but HanniGram never implemented it —
  a broken contract today. Added as **T20**.
- HanniGram's README says injection happens "when a task is active", but `readProjectMemory`
  is not gated on an active sheet and injects whenever the project has matching observations.
  Added as **T22**.

**Task additions**

- [x] **T20** Implement `mem_update` (update an observation by id, preserving `created_at_utc`
      and re-scoping through the same `topic_key` rules) and expose it in the extension, so the
      reference in the sODD skill stops being a broken contract.
- [x] **T21** Global injection budget: at most a small fixed number of pinned global heuristics
      enter the automatic context path; everything else global is retrieved on demand.
- [x] **T22** Fix the doc/code discrepancy about when memory is auto-injected.
      (Slice 5 — HanniGram's own README was corrected. The exact "cuando hay una tarea
      activa" claim is **not** in HanniGram: it lives in the HanniPI repo and is reported
      in the Log instead of being edited here.)

## Tasks

### Slice 1 — correctness and tests

- [x] **T1** Tests: add `tests/HanniGram.Core.Tests` (xUnit), all store tests against a temp
      db file. Cover upsert semantics, FTS search, empty/short query, limit clamp, delete,
      sessions. Wire `dotnet test` into `.github/workflows/ci.yml`.
- [x] **T2** Fix empty-MATCH crash: return no hits when the escaped query has no term
      >= 3 chars; never issue `MATCH ''`. Add an exception handler so the daemon returns a
      JSON error instead of a bare 500. Test: `q=ab`, `q=a`, `q=` → 200 `[]`.
- [x] **T3** Index `what`, `why`, `where_`, `learned` in `observations_fts`; bump
      `SchemaVersion` to force the one-off re-index. Test: a term present only in `what` is
      found by search.
- [x] **T4** Order by real recency: `updated_at_utc` for `ListObservations` and
      `FindLatestByTopicKey`. Test: upserted observation moves to the front.
- [x] **T5** Clamp `limit` in `Search`, `ListObservations`, `SuggestTopicKeys`,
      `ListSessions` (`Math.Clamp(limit, 1, 500)`). Test: `limit=-1` is capped.
- [x] **T6** Replace `RebuildFts()` with FTS5 triggers (`AFTER INSERT` / `AFTER DELETE` /
      `AFTER UPDATE`, using the `'delete'` command with OLD values) so indexing is O(1) and
      transactional; wrap mutating store operations in a transaction. Test: index stays
      consistent after save/update/delete without any rebuild call.
- [x] **T7** Sessions: unique index on `(project_id, session_id)`, created after
      deduplicating legacy rows (prefer a row with a summary, else the latest
      `started_at_utc`); the index is re-asserted on the already-migrated path too.
      `StartSession` is idempotent (`INSERT ... ON CONFLICT DO NOTHING` + read-back), so
      `EndSession` updates exactly one row. Test: two `session_summary` calls for one
      session id produce one row and the latest summary.
- [x] **T8** `FtsTrigramFallback`: build trigrams per token (no cross-word grams), dedup
      with a `HashSet`, one consistent per-token cap, and AND the per-token OR-groups so
      every query term must be present while a typo inside one token still matches. Test:
      a multi-word typo query still matches; an absent token is not satisfied by another.
- [x] **T9** CLI: read `HANNIGRAM_CWD` and the `project [cwd]` positional argument, via a
      testable `HanniGram.Core.Cli.CwdResolver`. Test: the helper's precedence and the
      positional read.

### Slice 2 — scopes

- [x] **T10** Global scope: reserve the `_global` sentinel project; add `scope`
      (`project|global`) to `mem_save` and the observations endpoint; add `mem_promote` to
      move an observation from project to global (preserving/rewriting `topic_key`).
- [x] **T11** Cross-scope search: `mem_search` / `/api/search` accept
      `scope=project|global|all` (default `all`), returning global hits marked with their
      origin so the model can tell a repo fact from a universal heuristic.
- [x] **T12** Global dedup: upsert by `topic_key` works identically in the global scope, so a
      heuristic taught twice updates in place instead of accumulating.

### Slice 3 — review loop (no token overhead)

- [x] **T13** Lifecycle: `state` column (`active|needs_review|archived`) plus `pinned`.
      Search and context filter to `active` by default; archived is excluded unless asked.
- [x] **T14** Cheap deterministic review signal on save: when a save with a `topic_key`
      updates an existing row, or when an unkeyed save has a high-similarity title in the same
      scope, return a compact `review` hint in the save response (a few tokens, no dump).
- [x] **T15** `mem_review` tool: bounded listing of `needs_review` / duplicate candidates with
      truncated previews; plus `mem_delete` (already present) and archive as the safe default.

### Slice 4 — MCP and portability

- [x] **T16** stdio MCP server reusing `HanniGram.Core` directly (no daemon dependency),
      mirroring the `mem_*` tool surface. Must coexist with the daemon via the existing
      WAL + `busy_timeout` design. (see Slice 4 in the Log)

### Slice 5 — integration and docs

- [x] **T17** Pi extension: expose the new tools and scope parameter; keep prompt text
      minimal so tool descriptions do not inflate every request. (Slice 5 — 14 tools;
      no automatic injection was added.)
- [ ] **T18** HanniPI integration: bump `hannigram-version.json` and the packaged asset.
- [x] **T19** Docs: README (scopes, review loop, MCP), fix the committed
      `.hannigram/memory.md` that still points at another machine's db path. (Slice 5 —
      the export no longer emits an absolute database path and was regenerated.)

## Acceptance criteria

- Every task's test passes and `dotnet test` runs in CI.
- `q=ab` / `q=a` / `q=` return `200 []`, never 500.
- A term stored only in `what` is retrievable by search.
- An upserted observation is the most recent result.
- `limit=-1` cannot dump the corpus.
- No code path calls a full FTS rebuild on write.
- A memory can be saved directly to the global scope and found from a different project.
- No tool response returns unbounded memory text.

## Checks

- `dotnet build HanniGram.slnx -c Release` then `dotnet test`.
- End-to-end against a **built-from-source** daemon on a **temporary db and non-default port**
  (the live daemon on `127.0.0.1:8765` and the user's real db must not be touched).
- Exercises: short-query 500, `what`-only retrieval, upsert recency, limit clamp, session
  dedup, global save + cross-project retrieval.

## Delivery

- Forecast: ~1200-1600 authored changed lines across 5 slices → exceeds the ~400 heuristic.
- Strategy: `ask-on-risk` → must ask once for the chain strategy before the first PR.
- Slice boundaries are PR boundaries. Work-unit commits per task on `feat/memory-v2`.

## Log

- 2026-10-15 — Branch `feat/memory-v2` created from `main` @ `373cc7d`. Audit findings
  recorded (Engram obs #238). Feature document created before the first source write.

### Slice 1 — implemented and verified (2026-10-15)

Work-unit commits on `feat/memory-v2` (tests ship inside the commit that changes the
behaviour they verify, so every commit is green on its own):

| Task | Commit | Summary |
|---|---|---|
| T1 | `ea4ca0e` | `test(core): add xUnit store test project and run tests in CI` |
| T2 | `719c75f` | `fix(core): never issue an empty FTS5 MATCH and return JSON daemon errors` |
| T3 | `1510205` | `feat(core): index what/why/where/learned in the FTS index` |
| T4 | `b844c40` | `fix(core): order observations by real recency using updated_at_utc` |
| T5 | `aa48421` | `fix(core): clamp caller-supplied limits before they reach SQL` |
| T6 | `2cf8fe8` | `perf(core): sync the FTS index with triggers instead of rebuilding it` |

Observed evidence (`dotnet --version` = `10.0.401`; restore succeeded from the local NuGet
cache — the only restore noise is `NU1900` for the unreachable private feed
`treball.original-soft.com`, which does not fail the restore):

- `dotnet build HanniGram.slnx -c Release --nologo` → 0 errors (warnings: `NU1900`, plus the
  pre-existing `CS8604` in `Program.cs`).
- `dotnet test HanniGram.slnx -c Release --nologo` → 41 passed, 0 failed.
- Each fix's regression tests were run against the pre-fix code and failed there, confirming
  they actually detect the defect: T2 4/5 errored with `fts5: syntax error near ""`
  (the 5th, a tab-only query, passed pre-fix); T3 3/3 failed; T4 2/2 failed; T5 7/7 failed;
  T6 `WritePath_DoesNotRebuildTheWholeIndex` failed on the rebuild-on-write code.
- End-to-end against a daemon built from source, `HANNIGRAM_DB` = temp file,
  `HANNIGRAM_URLS=http://127.0.0.1:8798`, temp dir removed afterwards; the user's real db
  (`~/.hannigram/hannigram.db`, mtime 09:30, before this session) and the live daemon on
  8765 were never touched:
  - `GET /api/search?project=e2e&q=ab` → **HTTP 200 `[]`**; `q=a` → **HTTP 200 `[]`**;
    `q=` → **HTTP 200 `[]`** (previously 500).
  - POST with `what="zephyrwidget handling"` (word in no other field), then
    `q=zephyrwidget` → **HTTP 200**, one hit id=1. Proves T3.
  - Upsert of `topicKey=e2e/topic` after an unrelated insert → `GET /api/observations`
    (count=2) returned the upserted row first (`id=1 title='E2E note v2'`). Proves T4.
  - `GET /api/observations?project=e2e&limit=-1` → **HTTP 200, count=1** (not the whole
    corpus). Proves T5.
  - Insert → `obsidianwalnut` found; update → `obsidianwalnut` gone, `geraniumposts` found;
    delete → `geraniumposts` gone. All with no rebuild on the write path. Proves T6.

### What changed the plan (Slice 1)

- **T3 + T6 were kept as separate commits.** They touch the same FTS schema, but T3 is a
  self-contained schema/index change (column list + `SchemaVersion` bump, migrating via the
  existing one-off guard) and T6 is the indexing mechanism (triggers + transactions). Two
  commits keep the migration reviewable apart from the write-path change.
- **T1 ships the scaffolding plus the tests that pass against the unfixed code.** Tests for
  behaviour that does not exist yet cannot live in a green T1 commit, so each later task
  lands its own regression test with its fix. The T1 test matrix (upsert, FTS search,
  empty/short query, limit clamp, delete, sessions) is therefore complete across T1..T6.
- **The migration's "drop triggers before the table" ordering is load-bearing, and the reason
  is different from the one written in the task.** The documented failure was reproduced (a
  trigger pointing at a dropped `observations_fts` makes the next write fail with
  `SQLite Error 1: 'no such table: main.observations_fts'`), but because the migration
  recreates the table under the same name, a dangling trigger alone does not break the next
  write. The real, reproducible failure is that `CREATE TRIGGER IF NOT EXISTS` *keeps a stale
  trigger body forever* — verified: with `DropFtsSyncTriggers()` removed, a database carrying
  an old `observations_fts_ai` keeps it and quietly stops indexing writes. The explicit drop
  is what guarantees the new trigger bodies take effect; the documented dangling case is
  guarded by the same ordering.
- **An intermediate build (T3) wrote `user_version = 2` without triggers.** `Initialize` now
  re-asserts the trigger triple with `CREATE TRIGGER IF NOT EXISTS` on the already-migrated
  branch, so a database migrated by that intermediate build still gets indexing.
- **Discovery relevant to T8:** `FtsTrigramFallback` ORs the query's trigrams whenever the
  strict AND match is empty, so two words that merely share a trigram (e.g. `alphaword` /
  `gammaword`) match each other. Test fixtures must be trigram-disjoint or a "term was
  removed from the index" assertion false-positives. This is the cross-word-gram sloppiness
  T8 is meant to fix; until then the tests document the constraint explicitly.
- **New indexes.** `idx_obs_project_updated` on `(project_id, updated_at_utc DESC)` supports
  the T4 ordering; `idx_obs_topic` is unchanged. No index was dropped.

### Slice 1 continued — T7 / T8 / T9 implemented and verified (2026-10-15)

Work-unit commits on `feat/memory-v2`:

| Task | Commit | Summary |
|---|---|---|
| T7 | `5a9c377` | `fix(core): enforce one session row per project and session id` |
| T8 | `5bf34d6` | `fix(core): build trigram fallback per token and AND the token groups` |
| T9 | `b802cb5` | `fix(cli): honour HANNIGRAM_CWD and the positional project cwd` |

Observed evidence (`dotnet --version` = `10.0.401`):

- `dotnet build HanniGram.slnx -c Release --nologo` → 0 errors, 9 warnings (all `NU1900` for
  the unreachable private feed; no compiler warning introduced).
- `dotnet test HanniGram.slnx -c Release --nologo` → **58 passed, 0 failed** (41 before this
  slice; +5 T7, +3 T8, +9 T9).
- Each fix was temporarily reverted and its regression tests re-run before restoring, to prove
  the tests detect the defect:
  - **T7** (pre-fix store, SchemaVersion 2): 5 of 8 `SessionStoreTests` failed —
    `StartSession_IsIdempotentForTheSameSessionId` → `Assert.Equal Failure: Values differ`
    (Expected 1, Actual 2); `StartingTwiceThenEnding_UpdatesTheSingleRow` and
    `TwoSessionSummaries_ProduceOneRowAndTheLatestSummaryWins` and
    `ReopeningOnTheCurrentVersion_ReassertsTheSessionsUniqueIndex` → `Assert.Single Failure:
    The collection contained 2 items`; `ReopeningWithDuplicateSessions_DeduplicatesAndPrefersTheSummaryRow`
    → `Assert.Single Failure: The collection contained 3 items`.
  - **T8** (pre-token-grouping fallback): `Search_TwoTokensWhereTheSecondIsAbsent_ReturnsNothing`
    and `Search_TwoUnrelatedTokensInDifferentDocuments_DoNotMatchEachOther` both failed with
    `Assert.Empty Failure: Collection was not empty`. The single-token typo test
    `Search_TypoWithinAToken_StillMatchesViaTheFallback` passed pre-fix **by design** — it is the
    guard against over-fixing, not a regression detector.
  - **T9** (helper carrying the old rule): `Resolve_EnvBeatsCurrentDirectory` and the three
    positive `Positional_*` tests failed with `Assert.Equal Failure: Strings differ`.
- End-to-end against a **daemon built from source**, `HANNIGRAM_DB` = temp file,
  `HANNIGRAM_URLS=http://127.0.0.1:8799`; server stopped and temp dir removed afterwards. The
  real db (`~/.hannigram/hannigram.db`, mtime `2026-10-15T07:30:30Z`) and the live daemon on
  8765 were never touched — the db mtime is byte-identical before and after:
  - `/api/sessions/start` + `/api/sessions/end` twice for `e2e-session` →
    `GET /api/sessions` returned **count 1** with summary `second summary`. Proves T7.
  - POST `content="zephyrous payload"`, then `GET /api/search?q=zephyruos` (strict AND match
    empty) → **1 hit** with content `zephyrous payload` (trigram fallback exercised); then
    `GET /api/search?q=zephyrous kwyjibo` → raw response **`[]`**. Proves T8.
  - `dotnet HanniGram.Cli.dll project` run from the repo dir with `HANNIGRAM_CWD` = a temp dir →
    **`hannigram-e2e-cwd`**; `project <other-temp-dir>` → **`hannigram-positional-cwd`**
    (positional beats the env var); the same with `HANNIGRAM_CWD` unset → positional honoured.
    Proves T9.

### What changed the plan (Slice 1 continued)

- **T7 `EndSession` needed no code change.** Once `(project_id, session_id)` is unique, the
  existing single `UPDATE ... WHERE project_id AND session_id` matches exactly one row. T7 is
  therefore the index + dedup migration + idempotent `StartSession`; the task's "EndSession
  upserts" wording is satisfied causally. No insert-on-missing behaviour was added: nothing
  required it, and it would change semantics.
- **T8 test (c) cannot use two overlapping words.** The specified algorithm ORs a token's own
  grams, so in `(g1 OR g2 …) AND (h1 OR h2 …)` any trigram shared by the two tokens lets each
  document satisfy the *other* token's group — `alphaword` / `gammaword` (sharing `awa`, `wor`,
  `ord`) still match each other post-fix. The regression test that actually distinguishes fixed
  from broken is a two-token query whose words are **gram-disjoint** and live in **different
  documents**: pre-fix it returns both documents, post-fix it returns none. The test documents
  this.
- **The trigram-disjoint fixture comments stay.** A single absent token can still match via its
  own OR-group (that is exactly the required typo tolerance), so a "term removed from the index"
  assertion in `FtsTriggerTests` still needs fixtures that share no trigram. The T8 fix removes
  the *cross-token* merging, not single-token tolerance, so the earlier constraint is **not**
  obsolete and the workaround was not removed.
- **`user_version` is now 3.** `Initialize` calls `EnsureSessionsUniqueIndex()` in both branches,
  so the index is re-asserted on the already-migrated path exactly like the FTS trigger triple.
  The dedup runs only when the index is being created, so normal opens stay cheap.
- **`FtsQuery` and the fallback now share one whitespace tokenizer** (`QueryTokens`), so "which
  tokens are terms" has a single implementation.
- **Pre-existing uncommitted doc section.** The working tree already carried an uncommitted
  "HanniPI integration constraints" section (T20–T22) from the preceding session; it is a
  doc-only change to this same file and is folded into this slice's documentation commit instead
  of being left dirty or dropped.

### Slice 2 — scopes implemented and verified (2026-10-15)

Work-unit commits on `feat/memory-v2` (tests ship inside the commit that changes the behaviour
they verify):

| Task | Commit | Summary |
|---|---|---|
| — (infra) | `c6e923f` | `test(core): serialize the suite that clears the shared Sqlite pool` |
| T10 | `c67f781` | `feat(core): add the reserved global memory scope and a project guard` |
| T11 | `4c4749f` | `feat(core): return project and global hits together with their origin scope` |
| T12 | `3633dad` | `test(core): prove the global scope dedups by topic_key in place` |
| T10 (promote) | `1a12cdd` | `feat(core): promote a project memory into the global scope` |

`SchemaVersion` stays **3** — the global scope reuses the existing `projects` / `observations` /
`observations_fts` / upsert-by-`topic_key` machinery with no new table and no schema change.

Observed evidence (`dotnet --version` = `10.0.401`; restore from the local NuGet cache; the only
restore noise is `NU1900` for the unreachable private feed `treball.original-soft.com`):

- `dotnet build HanniGram.slnx -c Release --nologo` → 0 errors (9 warnings: 8 `NU1900`, plus the
  pre-existing `CS8604` in `Program.cs`).
- `dotnet test HanniGram.slnx -c Release --nologo` → **87 passed, 0 failed** (58 before this
  slice), stable across 12 consecutive runs.
- Each behaviour was temporarily reverted and its tests re-run before restoring, to prove the
  tests detect the gap:
  - **T10** (sentinel guard disabled in `ProjectResolver.Resolve`):
    `Resolve_NeverReturnsTheGlobalSentinel_ForADirectoryNamedLikeIt` failed
    `Assert.NotEqual() Failure: Strings are equal`.
  - **T11** (global namespace excluded from the read set): 3 cross-scope tests failed — the
    "all returns both origins" and "global hit is labelled" assertions.
  - **T12** (upsert disabled in the global scope): 2 `GlobalDedupTests` failed.
  - **Promotion** (merge branch disabled): `Promote_WithExistingGlobalTopicKey_...` failed
    `Assert.True() Failure`.
- End-to-end against a **daemon built from source**, `HANNIGRAM_DB` = temp file,
  `HANNIGRAM_URLS=http://127.0.0.1:8797`; server stopped and temp dir removed afterwards. The real
  db (`~/.hannigram/hannigram.db`) and the live daemon on 8765 were never touched — the db mtime
  was byte-identical before and after (`2026-10-15T07:30:30.5035170Z`); port 8797 confirmed closed.
  - Fresh-db reads (`scope=global`, `scope=all`) created **no** `_global` row: read paths never
    materialize the sentinel.
  - A memory saved with `scope=global` was retrieved from a **different** project:
    `scope=all` → 1 global hit, `scope=project` → 0, `scope=global` → 1. `scope=all` → 2 hits
    labelled `project` + `global`. Proves T10 + T11.
  - Promotion, **no collision** → `outcome=moved`, the memory is found from another project and is
    gone from the originating project (same id, `topic_key` preserved).
  - Promotion, **collision** → `outcome=merged`: the promoted content overwrote the existing global
    row in place (title `Collide v2`, content `betatronnew`), the new term is found, the removed
    term is gone, and `topicKey=heuristics/collide2` returns **1** hit (not duplicated); global
    count stayed consistent (4).
  - E2E caveat: the first collision check used `collideoldmarker` / `collidenewmarker`, which share
    trigrams, so the "removed" term still matched via the documented trigram fallback. Rerunning
    with trigram-disjoint `alpenglowold` / `betatronnew` gave the expected `0` hits for the removed
    term — the same fixture constraint recorded in Slice 1.

CLI / API surface (the MCP wrapper is Slice 4):

- `POST /api/observations` accepts `scope` in the body (`project` default, `global` writes the
  shared sentinel namespace).
- `GET /api/search` and `GET /api/observations` accept `scope=project|global|all` (default `all`).
- `POST /api/observations/{id}/promote?cwd=` returns `{ observation, merged, outcome:
  "moved"|"merged" }`.
- CLI: `hannigram save … [--scope project|global]`, `hannigram search … [--scope
  project|global|all]` (default `all`; each hit printed with its `(scope)`).

### What changed the plan (Slice 2)

- **The global namespace is a reserved sentinel project, not a second table.** `_global`
  (`HanniGram.Core.MemoryScopes.GlobalProjectName`) is a real row in `projects`; saves reuse the
  same upsert-by-`topic_key`, the same FTS triggers, and the same recency ordering. `SchemaVersion`
  therefore did not move.
- **Reads must never create the sentinel, and `Resolve` must never return it.** The store resolves
  read namespaces through `NamespacesFor(projectId, scope, defaultScope)`, which looks the sentinel
  up read-only (absent → no global hits); only the write path calls `GetOrCreateGlobalProject()`.
  `ProjectResolver.GuardAgainstSentinel` rejects `_global` as a resolved project so a directory
  named `_global` cannot be mistaken for the shared namespace.
- **Origin is derived from identity, not from the request.** `Observation.Scope` / `SearchHit.Scope`
  are stamped from the row's `project_id` via `ScopeOfProject`, so the id-based overloads and
  `NamespacesFor` always label a global row `global` even when addressed as a project id. A test
  (`SentinelAddressedAsAProject_IsStillLabelledGlobal`) pins this; the fix was exposed by a
  promotion test and folded into the promotion commit.
- **Promotion collision = overwrite-in-place, not reject.** When the destination global namespace
  already owns the `topic_key` (a different row id), the promoted content overwrites the existing
  global row and the redundant project row is deleted; the FTS triggers keep the index correct with
  no rebuild. No collision → the row is moved (same id, `topic_key` preserved). This is the
  "taught it 100 times" path: the latest teaching wins instead of accumulating duplicates.
- **The suite was serialized to kill a pre-existing flake.** `FtsTriggerTests` calls the
  process-wide `SqliteConnection.ClearAllPools()`, which races other xUnit classes because pools are
  keyed by the exact connection string; the observable symptom was an intermittent
  `ObjectDisposedException`. `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
  fixes it (12/12 stable runs). This is infrastructure, not scope behaviour, so it is its own commit
  (`c6e923f`) ahead of T10.

### Slice 3 — review loop implemented and verified (2026-10-15)

Work-unit commits on `feat/memory-v2` (tests ship inside the commit that changes the behaviour they
verify, so every commit is green on its own):

| Task | Commit | Summary |
|---|---|---|
| T13 | `f891e9a` | `feat(core): add an observation lifecycle state and pinned ordering` |
| T14 | `a453cae` | `feat(core): report a compact near-duplicate hint on save` |
| T15 | `0c4dc0a` | `feat(core): add a bounded review surface and lifecycle endpoints` |
| T20 | `1e9a752` | `feat(core): implement mem_update to edit an observation by id` |
| T21 | `a04fe07` | `feat(core): expose a bounded global injection surface for context` |

`SchemaVersion` moves **3 → 4**. `observations` gains `state TEXT NOT NULL DEFAULT 'active'` and
`pinned INTEGER NOT NULL DEFAULT 0`. The FTS column set is unchanged, so the version bump still runs
the standard one-off re-index; a new idempotent `EnsureObservationLifecycleColumns()` (gated by
`PRAGMA table_info`) re-asserts the two columns on **both** the migrating and the already-migrated
path, and the new `idx_obs_project_state_updated` index is created only after the columns exist.

Observed evidence (`dotnet --version` = `10.0.401`; restore from the local NuGet cache; the only
restore noise is `NU1900` for the unreachable private feed `treball.original-soft.com`):

- `dotnet build HanniGram.slnx -c Release --nologo` → 0 errors (9 warnings: 8 `NU1900`, plus the
  pre-existing `CS8604` in `Program.cs`).
- `dotnet test HanniGram.slnx -c Release --nologo` → **122 passed, 0 failed** (87 before this
  slice; +10 T13, +7 T14, +5 T15, +7 T20, +6 T21).
- Each behaviour was temporarily reverted and its tests re-run before restoring, to prove the tests
  detect the gap:
  - **T13** (lifecycle columns + `active`-by-default filter reverted): 5 `LifecycleTests` failed
    (archived rows leaked into the default search/list; the `state=` selector returned nothing).
  - **T14** (save hint disabled): 3 `SaveReviewHintTests` failed (no `review` on a keyed upsert and
    on an unkeyed near-duplicate).
  - **T15** (review surface removed): 4 `ReviewSurfaceTests` failed.
  - **T20** (`UpdateObservation` / `PUT` reverted): 5 `UpdateObservationTests` failed.
  - **T21** (context surface removed): 4 `ContextSurfaceTests` failed.
- End-to-end against a **daemon built from source**, `HANNIGRAM_DB` = temp file,
  `HANNIGRAM_URLS=http://127.0.0.1:8796`; server stopped and the temp dir removed afterwards. The
  real db (`~/.hannigram/hannigram.db`) and the live daemon on 8765 were never touched:
  - Keyed upsert returns a compact, truncated `review` hint (kind `topic_key`); an unkeyed save
    whose title is a near-duplicate of an existing row returns a hint (kind `duplicate`).
  - An archived row is excluded from the default search and list, and returned again with
    `state=archived` and `state=all`. An unknown `state=` value → **HTTP 400**.
  - `PUT /api/observations/{id}` preserves `created_at_utc`, moves `updated_at_utc`, treats an
    empty string as "clear", and returns **404** for another project's id (scoped by project).
  - Pin + `GET /api/context` enforce the caps at **3** pinned global heuristics and **6** recent
    project rows, each row a truncated preview (never a body).
  - `GET /api/review` is bounded to **≤ 50** items, each preview **≤ 201** characters.

CLI / API surface (the MCP wrapper is Slice 4):

- `GET /api/observations` and `GET /api/search` accept `state=active|needs_review|archived|all`
  (default `active`); `GET /api/review` is the bounded review listing.
- `POST /api/observations/{id}/state?cwd=&scope=` sets `{ "state": ... }`;
  `POST /api/observations/{id}/pin?cwd=&scope=` sets `{ "pinned": true|false }`.
- `PUT /api/observations/{id}?cwd=&scope=` updates an observation (nullable fields: null = leave
  unchanged, empty string = clear) — this is `mem_update`.
- `GET /api/context?cwd=` is the bounded automatic-injection surface (no `limit` parameter).
- CLI: `hannigram update|state|pin|review`, plus `--state` on `search`/`list`.

The **T21 contract** (the hard budget on automatic injection):

- Endpoint: `GET /api/context?cwd=` (project resolved from the daemon like every other read; no
  `scope` and **no `limit` parameter by design** — a caller can never widen the budget).
- Caps (public constants on `HanniGramStore`, the single place to change the budget):
  `MaxPinnedGlobalHeuristics = 3`, `MaxRecentProjectRows = 6`. The review cap is
  `MaxReviewItems = 50`.
- Pinned-global half — SQL equivalent:
  `SELECT id, title, content, topic_key, type, pinned FROM observations WHERE project_id = _global AND pinned = 1 AND state <> 'archived' ORDER BY updated_at_utc DESC, id DESC LIMIT 3`.
- Recent-project half — SQL equivalent:
  `SELECT id, title, content, topic_key, type, pinned FROM observations WHERE project_id = <project> AND state = 'active' ORDER BY pinned DESC, updated_at_utc DESC, id DESC LIMIT 6`.
- Both halves return previews only: title ≤ 80 chars, context preview ≤ 160 chars. The observation
  body never leaves the store on this surface.

### What changed the plan (Slice 3)

- **T13 ships the store + model only; T15 owns every lifecycle endpoint.** The `state`/`pin` HTTP
  routes, the `state=` query params, and the CLI `state`/`pin`/`review` commands are attributed to
  T15 (the "review loop" task), leaving T13 as a pure schema/model/ordering commit. This keeps the
  T13 diff about persistence and T15 about the surface.
- **Default read state is `active` only; archived is the safe forget.** `state=` accepts
  `active|needs_review|archived|all`, and archived is excluded unless explicitly asked. `archive`
  is therefore the non-destructive default over `delete`. `GetByTopicKey` stays unfiltered so the
  full history of a key is still reachable.
- **Upsert targets the latest non-archived row for a `topic_key`.** Saving a key whose only row is
  archived inserts a new active row instead of reviving the archived one; this matches "latest
  teaching wins" without silently un-archiving.
- **`SetObservationState` / `SetObservationPinned` do not move `updated_at_utc`.** A lifecycle
  change is metadata, not a re-teach, so it must not reorder recency. Recency ordering itself gained
  `pinned DESC` in `ListObservations` and in the context ordering; `Search` stays ordered by bm25.
- **`SaveObservation` keeps its 2-tuple and delegates.** The review hint is additive:
  `SaveObservationWithReview` returns `(Observation, bool Created, SaveReviewHint? Review)` and the
  old signature forwards to it, so no existing caller changes. Hint kinds are `topic_key` (a keyed
  save overwrote an existing row) and `duplicate` (an unkeyed save is a near-duplicate in the same
  scope).
- **Similarity is deterministic and bounded.** `MemoryDedup` normalizes to alphanumeric tokens
  (len ≥ 2) over title + content and computes Dice `2|A∩B| / (|A|+|B|)`; a row is a near-duplicate
  when both `MinSignatureTokens` (3) and Dice ≥ `NearDuplicateThreshold` (0.8); the scan is bounded
  by `WindowSize` (50). No LLM, no embeddings.
- **T20 `UpdateObservation` uses nullable overrides** (null = unchanged, empty string = clear),
  preserves `created_at_utc`, moves `updated_at_utc`, and is scoped by project. A `topic_key`
  collision makes the updated row win and leaves the other row unkeyed — non-destructive, no
  duplicate and no dropped data.
- **The sODD skill's `mem_update` reference stops being a broken contract.** `sodd/SKILL.md`
  referenced `mem_update`, which HanniGram never implemented. T20 adds it (store + `PUT` + CLI);
  HanniPI and `pi-extension/index.ts` are untouched here (Slice 5 wires the extension).
- **The whole slice was reconstructed task-by-task forward from HEAD to guarantee every commit is
  green.** After the split the final tree was diffed byte-for-byte against the pre-split working
  tree to confirm identity.

### Slice 4 — T16 (stdio MCP server) implemented and verified (2026-10-15)

Work-unit commits on `feat/memory-v2` (each is green on its own; tests ship with the code they
verify):

| Task | Commit | Summary |
|---|---|---|
| T16 | `7a1360b` | `feat(mcp): add a stdio MCP server over HanniGram.Core` |
| T16 (release) | `c278fb8` | `build(release): ship the MCP server in the Windows asset` |
| T16 (docs) | `b1aa933` | `docs: document the opt-in stdio MCP server` |
| T16 (log) | _(this entry)_ | `docs: record the T16 MCP slice in the feature log` |

New project `src/HanniGram.Mcp` (hand-rolled JSON-RPC 2.0, **no new NuGet dependency**: framing,
dispatch and the tool layer are built on `System.Text.Json`), plus a second test project
`tests/HanniGram.Mcp.Tests` (a plain library that `ProjectReference`s the MCP exe). Both are
registered in `HanniGram.slnx`.

Observed evidence (`dotnet --version` = `10.0.401`):

- `dotnet build HanniGram.slnx -c Release --nologo` → **0 errors** (the same `NU1900` noise for the
  unreachable feed `treball.original-soft.com`, plus the pre-existing `CS8604` in the Server).
- `dotnet test HanniGram.slnx -c Release --nologo` → **180 passed, 0 failed** (122 Core + 58 MCP).
- Four regressions were temporarily reverted and re-run, then restored (store confirmed untouched
  by `git diff --stat`; no `TEMP REVERT` markers remain):
  - **Bounded projection** (disable `Bounded.Text` truncation): `BoundedResultTests
    .GetObservation_TruncatesAnOversizedBody` failed (1 failed / 3 passed).
  - **Framing** (`WriteIndented = true`): `Framing_WritesOneMessagePerLine_WithNoEmbeddedNewlines`,
    `TheStreamIsAnsweredPerLine_NotPerChunk` and `StdoutCarriesOnlyProtocolMessages_
    AndDiagnosticsGoToStderr` failed (3 failed).
  - **stdout purity** (route the ready line to stdout): the same stdout-purity test failed (1 failed).
  - **Dispatch** (unknown method returns a normal result instead of an error):
    `UnknownMethod_IsMethodNotFound` failed (1 failed).
- **The coexistence revert did not fail any test, and that is the finding.** Removing the
  `busy_timeout` pragma left a decisive probe green: a `DefaultTimeout = 0` connection against a
  blocker holding `BEGIN IMMEDIATE` + `INSERT` printed `PROBE outcome=INSERT SUCCEEDED
  elapsedMs=325`. `Microsoft.Data.Sqlite` retries on `SQLITE_BUSY` by itself (and
  `DefaultTimeout = 0` means "wait indefinitely", not "fail fast"), so the coexistence guarantee is
  **provider-enforced** and the store pragma is redundant belt-and-braces. The probe was removed
  after the observation.
- **End-to-end over real stdio** (temp db, temp project dir, and the HTTP daemon on a random free
  port open on the *same* temp db):
  - `initialize` echoed the client's `2025-06-18`, returned capabilities and
    `serverInfo { name: "hannigram", version: "1.0.0" }`; `notifications/initialized` produced **no
    line** (notifications are never answered).
  - `tools/list` returned all **14** tools.
  - `mem_save` (scope `global`, project alpha) → `id=2, created=true, scope=global`.
  - `mem_search daemonsidewrite` from project alpha → **1 hit** (the daemon's write, scope
    `project`); `mem_search mcpglobalwrite` from project beta → **1 hit** (the global row). That is
    the cross-scope, cross-process read proven in both directions.
  - stdout was **5 lines, all `jsonrpc:"2.0"`, 0 non-JSON-RPC lines**; the diagnostics
    (`stdio server ready`, `initialize client=…`, `notification notifications/initialized`,
    `stdin closed`) appeared **only on stderr**; exit code 0.
  - The daemon (same temp db) then searched `mcpglobalwrite` → 1 hit (global) and `daemonsidewrite`
    → 1 hit, with **no `SQLITE_BUSY`** and consistent reads — coexistence confirmed in the reverse
    direction too.
- **Cap exceedance at process level**: a 6000-char body came back as length **2001** (2000 + the
  `…` marker) from both the `mem_save` echo and `mem_get_observation`. The bound holds.
- **`scripts/pack-asset.ps1`** ran to completion (with `HANNIGRAM_DB` pointed at a temp file):
  published Server + CLI + MCP, the daemon smoke test passed on `127.0.0.1:8799`, and the new MCP
  smoke test (`initialize` + `tools/list` over stdio) passed. The zip
  `artifacts\hannigram-windows-x64.zip` (110.5 MB, sha256
  `abe62ff14bad7a3f44dae02c5f9dd82e2e858864a833f66151e37fd45926c2f0`) contains
  `appsettings.json`, **`hannigram-mcp.exe`**, `hannigram.exe`, `HanniGram.Server.exe`,
  `LEEME.txt`, `README-HanniGram.md`, `VERSION.txt`, `pi-extension/index.ts`,
  `pi-extension/package.json`. The asset stayed **additive**: the daemon smoke test and the existing
  entries are unchanged.

#### The T16 contract

- **Transport:** newline-delimited JSON-RPC 2.0 on stdin/stdout. stdout carries **only** protocol
  messages; every log line goes to stderr (hard rule, tested).
- **Protocol version:** the server declares the current stable `2025-11-25`, tolerates the client's
  requested version, and falls back to `2025-11-25` for anything newer or unknown — `initialize`
  never errors.
- **Errors:** `-32700` parse error (with `id: null`), `-32601` unknown method, `-32602` unknown tool
  or missing/mistyped argument; a not-found row or an unknown lifecycle value is a tool result with
  `isError: true`. Notifications get no reply.
- **Tools (14, sorted):** `mem_context, mem_current_project, mem_delete, mem_get_observation,
  mem_pin, mem_promote, mem_review, mem_save, mem_search, mem_session_summary, mem_state,
  mem_suggest_topic_key, mem_timeline, mem_update` — each maps onto an existing store method, with
  no reimplemented logic.
- **Scope/project resolution:** the per-call `project` argument, then `HANNIGRAM_CWD`, then the
  process cwd; `HANNIGRAM_DB` with the same default as the daemon and CLI.
- **Bounds:** `MaxItems = 50`, `MaxTitleChars = 200`, `MaxFieldChars = 2000`,
  `MaxTokenChars = 200`; `limit` is clamped (not rejected) into `[1, 50]`. No tool ever returns an
  unbounded body (`GetByTopicKey` is only bounded by the `mem_timeline` cap).

#### What changed the plan (Slice 4)

- **The coexistence guarantee is not the store's pragma.** The planned "remove `busy_timeout`,
  watch the test fail" revert proved nothing because the provider already retries on `SQLITE_BUSY`.
  The honest statement is that coexistence is provider-enforced; the store pragma stays as
  belt-and-braces and the e2e (daemon + MCP on one temp db, reads both ways, no `SQLITE_BUSY`) is
  the real evidence.
- **The MCP project is an `Exe` with an explicit `static int Main`.** A top-level-statements `Program`
  cannot be `ProjectReference`d by a test project, and the requirement asked for tests of the new
  code; the explicit `Main` keeps the exe referenceable while staying a normal console entry point.
- **Test fixtures must be trigram-disjoint.** FTS5 "absent term" assertions used terms that shared
  trigrams with the seeded corpus and matched unexpectedly; the fixtures were reworked to disjoint
  markers (`alpenglowsync`, `betatronlayout`, `quartzpromote`, `kestrelstale`, `daemonsidewrite`,
  `mcpglobalwrite`, `quartzlongmarker`, `clampsearchmarker`, `coexistmarker`, `lockwaitmarker`,
  `walreadmarker`).
- **`instructions` was omitted from `initialize` and every tool description is ≤ 300 chars.** The MCP
  surface enters a model's prompt, so T16 keeps it thin on purpose (this is the same "no token
  overhead" thread as T15).

### Slice 5 — T17 / T19 / T22 + release preparation (2026-10-15)

Work-unit commits on `feat/memory-v2`:

| Task | Commit | Summary |
|---|---|---|
| T17 | `84dd791` | `feat(pi-extension): expose the memory scopes, lifecycle and update tools` |
| T19 (export) | `192c9ba` | `fix(cli): keep the local database path out of the memory export` |
| T19 (docs) | `839527b` | `docs: document the memory scopes, cross-scope search and the review loop` |
| release prep | `c4806f8` | `chore(release): add the changelog and bump the extension to 0.2.0` |
| T17/T19/T22 (log) | _(this entry)_ | `docs(core): record the slice 5 evidence in the feature log` |

The extension now registers **14** tools — `mem_current_project, mem_save, mem_delete, mem_search,
mem_context, mem_timeline, mem_get_observation, mem_suggest_topic_key, mem_session_summary,
mem_update, mem_review, mem_promote, mem_state, mem_pin` — the same surface the MCP server exposes.
`mem_save` gained `scope=project|global`, `mem_search` gained `scope=project|global|all` (default
`all`, each hit rendered with its origin), the by-id mutations (`mem_update`, `mem_state`,
`mem_pin`) accept `scope`, and the save response surfaces the compact `review` hint. Every
description stays short and decision-oriented, no tool was added that injects memory text on its
own, and no argument was added that nothing uses.

Observed evidence (`dotnet --version` = `10.0.401`, `node --version` = `v24.14.0`; restore from the
local NuGet cache; the only restore noise is `NU1900` for the unreachable private feed
`treball.original-soft.com`):

- `dotnet build HanniGram.slnx -c Release --nologo` → **0 errors** (12 warnings: 11 `NU1900`, plus
  the pre-existing `CS8604` in `Program.cs`).
- `dotnet test HanniGram.slnx -c Release --nologo` → **180 passed, 0 failed** (122 Core + 58 MCP),
  unchanged from Slice 4.
- `node --experimental-strip-types --no-warnings verify-extension.mjs` against a **daemon built from
  source** (`HANNIGRAM_DB` = temp file, `HANNIGRAM_URLS=http://127.0.0.1:8791`, jiti loads the real
  `index.ts`) → **exit 0**, `Registered 14 tools`, `Tool list matches the expected 14 tools`,
  `ALL TOOLS EXECUTED OK`. The run exercised every new capability through the extension's own HTTP
  calls and asserted the results:
  - `mem_save … scope=global` → `Saved observation #2 (global)`.
  - the same global row read back from a **second project cwd** with `scope=all` → labelled
    `(global)` (asserted on `details[].scope === "global"`).
  - `mem_update {id:1, learned:…}` → `Updated observation #1`.
  - `mem_state {id:3, state:needs_review}` → `Observation #3 state: needs_review`, and
    `mem_review` then listed that same id.
  - `mem_promote {id:3}` → `Promoted #3 to global (moved)`.
  - `mem_pin {id:2, pinned:true, scope:global}` → `Observation #2 pinned: true`.
- Raw HTTP transcript against that daemon (fresh temp db, `Invoke-WebRequest` bodies quoted):
  - `POST /api/observations?cwd=<alpha>` → `200 {"observation":{…,"state":"active","scope":"project"},
    "created":true,"scope":"project"}`.
  - `GET /api/search?cwd=<beta>&q=vrmxapricotdelta&scope=all` → **`200 []`** (a project namespace is
    not visible from another project).
  - `POST /api/observations/{id}/promote?cwd=<alpha>` → `200 {…"projectId":2,"scope":"global"},
    "merged":false,"outcome":"moved"`.
  - `GET /api/search?cwd=<alpha>&q=vrmxapricotdelta&scope=project` → **`200 []`** (the row left the
    project) and `GET /api/search?cwd=<beta>&q=vrmxapricotdelta&scope=all` → **`200 [{… "id":4 …,
    "scope":"global"}]`** — the promoted memory is retrievable cross-project with no FTS rebuild.
  - `PUT /api/observations/4?cwd=<alpha>` → `createdPreserved=true`, `updatedMoved=true`;
    `POST …/state {state:needs_review}` → `"state":"needs_review"`; `POST …/pin?scope=global
    {pinned:true}` → `"pinned":true`; `GET /api/review?cwd=<alpha>&scope=all` → a bounded item list
    with `needs_review` rows and truncated previews.
  - Historical note: two earlier probe queries were misleading for the reasons already recorded in
    Slices 1 and 3 — a flagged row (`needs_review`) is excluded from a default search, and a
    single-token query still matches through the documented trigram fallback. The checks above use a
    trigram-disjoint marker and an active row.
- `.hannigram/memory.md` regenerated with `hannigram init --cwd <repo>` against a **copy** of the
  user's database (the real file is held by the live daemon; it was only ever copied from, and its
  mtime was identical before and after: `2026-10-15T07:30:30Z`). Result:
  `# Memoria del proyecto — AlPeFe/HanniGram` / `BD local (fuente de verdad):
  ~/.hannigram/hannigram.db — ruta configurable con HANNIGRAM_DB.` / `Observaciones: 0`. A grep for
  `alexlocal`, `C:\Users` and `/Users/` in the file finds **nothing**.
- Version: `pi-extension/package.json` `0.1.0` → **`0.2.0`**; the README tag example `v0.1.0` →
  `v0.2.0`. `.github/workflows/release.yml` and `scripts/pack-asset.ps1` carry no version constant
  (they take the tag / `-Version` at build time, and their `v0.1.0` occurrences are usage examples in
  a description and a comment). No workflow trigger or permission was touched.

#### T22 — where the claim actually lives

The exact claim is **not** in HanniGram. It is in the HanniPI repo, which this slice must not edit:

> `C:\Users\alexp\source\repos\HanniPI\README.md`, line 34:
> `- **Inyección automática** — cuando hay una tarea activa, las observaciones recientes del proyecto se inyectan al contexto para que el agente recuerde.`

Reality, verified in `HanniPI/packages/coding-agent/src/extensions/sodd/index.ts`:
`readProjectMemory` (line 95) is called from the `pi.on("context")` handler (line 310), which fires
before **every** LLM call, and the only guard is `if (adr.length === 0 && context.length === 0)
return;` (line 311). There is no dependency on an active sheet, so the harness injects whenever the
project has matching observations. HanniGram's own README was corrected accordingly (it previously
said the injection happens "al reabrir un proyecto" and reported an index of "número de ADR, temas,
última decisión", which is not what the hook pushes: it pushes up to 12 ADR rows and up to 6 recent
rows as `title (topic_key): learned`).

### What changed the plan (Slice 5)

- **The committed export is a fossil from another machine.** `.hannigram/memory.md` was written on
  2026-10-08 against `C:\Users\alexlocal\.hannigram\hannigram.db` with one observation
  (`#2 test trigram pastel rosado`). The database on **this** machine is a different one: it is at
  `user_version` 1 and holds three projects (`stage`, `demo`, `OriginalSoft/Gam.A3`) and exactly one
  observation, which belongs to `OriginalSoft/Gam.A3`. There is **no** `AlPeFe/HanniGram` row, so a
  faithful regeneration exports **0 observations** and the palette ADR disappears from the file.
  That is the honest result of "regenerate from real data"; hand-copying the old row back would be
  the fiction the task forbids. The follow-up decision (re-seed memory on the machine that owns the
  database, or drop the committed export) belongs to the orchestrator, not to this slice.
- **The task's premise about the tool descriptions was wrong.** The prompt described the extension's
  descriptions as "in Spanish"; they are in **English** (only the `ctx.ui.notify` strings are
  Spanish). The new descriptions stay in English to match the file, and the notify strings stay in
  Spanish to match the existing UI copy.
- **`mem_context` was left on `/api/observations`.** Its sibling `GET /api/context` (T21) is the
  *automatic* injection surface, and T21's consumer is HanniPI's context hook reading SQLite
  directly — not a tool the model calls. Re-pointing `mem_context` at it would have turned an
  existing on-demand contract (recent rows with content) into previews, which the slice was not asked
  to do. The bounded surface therefore stays a server capability; if the harness should also reach
  it, that is a follow-up.
- **`mem_search` did not gain a `state` filter.** The task listed exactly two parameter additions
  (`scope` on save and search) and the "do not add fields nobody uses" rule, so the
  archived/`needs_review` selector stays a CLI/API/MCP capability. `mem_review` covers the review side
  of the loop.
- **HanniPI has a hardcoded mem_* inventory that does not know the five new tools.**
  `HanniPI/packages/coding-agent/src/extensions/hanniorq/index.ts` lines 110-120 list the read and
  write tools used for its end-of-turn memory line; `mem_update`, `mem_promote`, `mem_state` and
  `mem_pin` are in neither set and `mem_review` is not in the read set. Nothing breaks (the detail
  helper falls back to the raw tool name), but the accounting under-reports. Reported, not patched:
  HanniPI is out of bounds for this slice.
- **`promote` keeps its own resolution rule.** The route resolves the project from `cwd` and takes no
  `scope` (a promotion only makes sense from a project namespace into the global one), so the
  extension's `mem_promote` deliberately takes a single `id` argument.
- **The regenerated export keeps the generator's LF endings.** The repository uses
  `core.autocrlf=true`, so the worktree file is checked out as CRLF while the index stores LF; the
  CLI writes LF and nothing else changed.

### Verifier findings F1-F4 — fixed and verified (2026-10-15)

An independent verifier ran against the slice 5 tree and reported four defects. Each is fixed as its
own work-unit commit, green on its own, with the pre-fix failure reproduced first.

| Finding | Commit | Summary |
|---|---|---|
| F1 | `989c2cd` | `fix(promote): refuse archived sources and preserve lifecycle state` |
| F2 | `9e2a341` | `fix(api): expose the canonical topic-key query route` |
| F3 | `b862450` | `fix(extension): bound the model-facing memory reads` |
| F4 | `cc546c4` | `fix(delete): let mem_delete reach the global scope` |

What each finding was, in one line:

- **F1 — promotion ignored the lifecycle.** `PromoteObservation` moved or merged an `archived`
  source and left the surviving global row `active`, so retired content became current truth for
  every project. It now returns a bounded `PromotionResult` (`moved` / `merged` / `refused` plus the
  reason `source_archived`): an archived source is refused by default (no write, no sentinel), the
  route answers `409`, the MCP tool returns an `isError` result, and `allowArchived=true` is the
  opt-in that lands the content `archived`. An `archived` global target is now found through
  `FindAnyByTopicKey`, so a live teaching revives it instead of inserting a second row.
- **F2 — slash topic keys were unretrievable over HTTP.** The only by-key route took the key as a
  path segment, so `encodeURIComponent("svc/auth")` never matched and callers got a silent `[]`. The
  canonical form is now `GET /api/observations/topic?topicKey=…`, with the path form kept for plain
  keys and as a catch-all route for raw slash paths, and a still-encoded key decoded before lookup.
- **F3 — model-facing reads were unbounded.** `mem_get_observation` returned the raw JSON of a row,
  so a 6000-character body reached the model intact. Every read tool now renders through the same
  budget as the MCP caps (2000 characters for free text, 200 for titles and tokens, an explicit
  ellipsis, and bounded `details` payloads), and `GetByTopicKey` carries its own hard cap of 50 rows
  because it is the only read that returns a key's whole history.
- **F4 — `mem_delete` could not reach global memory.** The extension's delete only ever addressed the
  caller's project; it now takes `scope`, and the server's DELETE route resolves `scope=global`
  read-only so a miss reports `404` instead of materialising the sentinel.

Observed evidence (`dotnet --version` = `10.0.401`, `node --version` = `v24.14.0`):

- Per-commit `dotnet build src/HanniGram.Server -c Release` → **0 errors**; per-commit test runs
  (Core / MCP): F1 → **127 / 59**, F2 → **127 / 59**, F3 → **130 / 59**, F4 → **130 / 59**, every one
  `Con error: 0`. The final tree is **189 passed, 0 failed** (130 Core + 59 MCP), up from 180.
- Pre-fix proof for every new regression test. The fix is reverted in place while its API is kept, so
  the tests compile and fail on **behaviour** rather than on a build error, and the tree is restored
  afterwards:
  - F1 (Store: the refusal guard, the archived-target lookup and the `state = $s` propagation
    dropped) → **4 failures, 126 passed** in Core:
    `Promote_ArchivedSource_IsRefusedAndLeavesTheLiveGlobalRowUntouched` (PromotionTests.cs:158),
    `Promote_ArchivedSource_RefusedByDefault_WritesNothingAndCreatesNoSentinel` (184),
    `Promote_ArchivedSource_WithOptIn_LandsArchived_AndNeverActive` (211),
    `Promote_ActiveSource_OverArchivedGlobalTarget_RevivesItToTheSourceState` (251). MCP →
    **1 failure, 58 passed**:
    `ToolSurfaceTests.Promote_ArchivedSource_IsRefusedUnlessAllowArchivedIsSet` (`Assert.True()`
    failure on the `isError` result).
  - F3 C# (the clamp and the SQL `LIMIT` wiring dropped) → **2 failures, 128 passed**:
    `TimelineBoundTests.GetByTopicKey_IsCapped_AtTheStoreLimit` (TimelineBoundTests.cs:25) and
    `GetByTopicKey_ClampsACallerLimit_SoItCannotBeRaised` (37), both `Assert.Equal() Failure: Values
    differ`.
  - F2 (the query route, the catch-all route and `mem_timeline`'s query form dropped) → the
    extension verifier stops at `verify-extension.mjs:185` with
    `mem_timeline returned nothing for the slash topic key 'svc/auth' (silent false negative)`, after
    the server answered `No observations for topic 'svc/auth'.`
  - F3 extension (every bounding call dropped) → the verifier stops at `verify-extension.mjs:217`
    with `mem_get_observation returned 6302 characters; the model-facing body must be bounded`.
  - F4 (the DELETE scope and `mem_delete`'s scope dropped) → the verifier stops at
    `verify-extension.mjs:240` with `mem_delete with scope='global' did not delete the global
    memory`, after the delete answered `Observation #7 not found.`
- `node --experimental-strip-types verify-extension.mjs` against a daemon **built from source** on a
  **fresh temp db and a non-default port**, run at the F2 commit (8795), the F3 commit (8796) and the
  F4 commit (8797) → **exit 0** in all three, each printing `Tool list matches the expected 14 tools`
  and `ALL TOOLS EXECUTED OK`. The three revert proofs above ran the same harness on ports
  8801 / 8803 / 8804.
- Raw-HTTP end-to-end at the final commit (port 8799, fresh temp db, the same source binary) →
  **31/31 checks passed**, covering all four findings:
  - the refusal: `409` with `outcome=refused` and `reason=source_archived`, the live global row
    unchanged (same title and state, still visible), the archived source still present, and no
    duplicate global row left behind;
  - the opt-in: `200`, `outcome=merged`, the surviving row `state=archived` carrying the source's
    content, and hidden from the default global listing;
  - all four topic-key forms (query raw, query percent-encoded, raw-slash path, `%2F` path) plus the
    plain-key path form and the missing-`topicKey` `400`;
  - the by-key cap: 55 rows seeded through SQLite (the write path upserts by `topic_key`, so a key
    cannot accumulate rows over HTTP) → the route returns 50, still 50 when the caller adds
    `limit=1000`, and 1 when the caller asks for `limit=1`;
  - the global delete: `scope=global` deletes the global row, reports `404` for a project row (which
    survives) and for a missing id, and the unscoped project delete still works.
- Hygiene: the live daemon on `127.0.0.1:8765` (pid 5880) and the user's real db were never touched —
  the db mtime is unchanged at `2026-10-15T07:30:30.5035170Z`. Every temporary daemon was stopped and
  every temp db removed; the only running `HanniGram.Server` process is pid 5880. Nothing was pushed:
  the four commits sit on `feat/memory-v2` locally.

#### What changed the plan (F1-F4)

- **The first draft of the F1 commit was rebuilt.** Its `pi-extension/index.ts` emitted the
  `allowArchived` parameter block inside `mem_promote`'s *return* object instead of its
  `parameters: Type.Object({…})` schema. The file still parsed — `allowArchived:` was just a stray
  property on a returned object — but the harness validates arguments against `parameters`, so the
  documented opt-in was not reachable from the model, and the returned payload carried a field no
  caller asked for. That is the same class of defect as F1 itself and it is invisible to
  `dotnet test`, which does not cover the extension. The four commits were therefore rebuilt from the
  verified final tree through an explicit hunk→finding map, and the branch now carries
  `989c2cd` / `9e2a341` / `b862450` / `cc546c4` instead of the earlier drafts.
- **Which finding owns a hunk is a judgement, not a fact.** `Program.cs`'s `Flag()` helper and
  `public partial class Program;` are F1 (the refusal needs the first; the second is the integration
  hosting hook), the topic routes are F2, and the DELETE scope is F4. The README sentence that
  carries both the `mem_delete` scope and the `mem_promote` opt-in is attributed to F1, because
  splitting one 1-line hunk would have meant inventing a hunk. Every stage was materialised, built,
  tested and committed on its own, so the split is proven rather than asserted.
- **Two facts invalidated the first version of the end-to-end script.** `GET /api/observations/{id}`
  is project-scoped only (it ignores `?scope=global`), and the write path upserts by `topic_key`.
  The final script therefore reads global rows from the global listing rather than by id, and seeds
  the cap rows directly in SQLite exactly as `TimelineBoundTests` does. One revert run was also
  discarded because the Release binary had not been rebuilt after the source was restored; the
  proofs above come from runs against a freshly built binary.
