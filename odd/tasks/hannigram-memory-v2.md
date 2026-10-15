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
- [ ] **T7** Sessions: unique index on `(project_id, session_id)`; `EndSession` upserts
      instead of updating all matching rows. Test: two `session_summary` calls for one
      session id produce one row and the latest summary.
- [ ] **T8** `FtsTrigramFallback`: build trigrams per token (no cross-word grams), use a
      `HashSet`, and make the collected/used cap consistent. Test: a multi-word typo query
      still matches.
- [ ] **T9** CLI: actually read `HANNIGRAM_CWD` (currently advertised in help, never used).

### Slice 2 — scopes

- [ ] **T10** Global scope: reserve the `_global` sentinel project; add `scope`
      (`project|global`) to `mem_save` and the observations endpoint; add `mem_promote` to
      move an observation from project to global (preserving/rewriting `topic_key`).
- [ ] **T11** Cross-scope search: `mem_search` / `/api/search` accept
      `scope=project|global|all` (default `all`), returning global hits marked with their
      origin so the model can tell a repo fact from a universal heuristic.
- [ ] **T12** Global dedup: upsert by `topic_key` works identically in the global scope, so a
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

