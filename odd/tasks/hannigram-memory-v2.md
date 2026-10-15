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

- [ ] **T20** Implement `mem_update` (update an observation by id, preserving `created_at_utc`
      and re-scoping through the same `topic_key` rules) and expose it in the extension, so the
      reference in the sODD skill stops being a broken contract.
- [ ] **T21** Global injection budget: at most a small fixed number of pinned global heuristics
      enter the automatic context path; everything else global is retrieved on demand.
- [ ] **T22** Fix the doc/code discrepancy about when memory is auto-injected.

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

- [ ] **T13** Lifecycle: `state` column (`active|needs_review|archived`) plus `pinned`.
      Search and context filter to `active` by default; archived is excluded unless asked.
- [ ] **T14** Cheap deterministic review signal on save: when a save with a `topic_key`
      updates an existing row, or when an unkeyed save has a high-similarity title in the same
      scope, return a compact `review` hint in the save response (a few tokens, no dump).
- [ ] **T15** `mem_review` tool: bounded listing of `needs_review` / duplicate candidates with
      truncated previews; plus `mem_delete` (already present) and archive as the safe default.

### Slice 4 — MCP and portability

- [ ] **T16** stdio MCP server reusing `HanniGram.Core` directly (no daemon dependency),
      mirroring the `mem_*` tool surface. Must coexist with the daemon via the existing
      WAL + `busy_timeout` design.

### Slice 5 — integration and docs

- [ ] **T17** Pi extension: expose the new tools and scope parameter; keep prompt text
      minimal so tool descriptions do not inflate every request.
- [ ] **T18** HanniPI integration: bump `hannigram-version.json` and the packaged asset.
- [ ] **T19** Docs: README (scopes, review loop, MCP), fix the committed
      `.hannigram/memory.md` that still points at another machine's db path.

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

