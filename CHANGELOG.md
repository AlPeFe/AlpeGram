# Changelog

All notable changes to HanniGram are documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- **Memory search relaxes `AND` to `OR` with bm25 ranking.** `HanniGramStore.Search` opened with a
  strict all-terms query, so one absent word turned a good query into zero hits (the "AND trap") and
  any paraphrase that shared no literal term returned nothing. The primary path is now an `OR` of the
  query terms ranked by bm25 — which still favours rows that satisfy more (and rarer) terms — and the
  trigram `OR`-of-grams fallback is kept as a second pass for typos and for the 1-2 character guard.
  Measured on the 42-query / 35-observation bench: recall@1 0.600 -> 0.800, recall@5 0.600 -> 0.875,
  semantic recall@5 0.000 -> 0.643, false-positive rate 0.000, p95 8.4 -> 6.9 ms. Trade-off to watch:
  a query now returns a candidate list instead of one row, so `mem_search` payloads grow (~196 -> ~1399
  bytes per query on the bench corpus). `mem_search`'s `query` description no longer promises
  "all terms must match".

## [0.2.1] - 2026-10-15

### Fixed

- **The Pi extension honours `HANNIGRAM_URLS`.** `baseUrl()` only read `HANNIGRAM_URL`, so a daemon
  started on another port (the verification harness, a second instance, a non-default port) was
  ignored and every call went to `127.0.0.1:8765` — where an older daemon silently answered without
  the new fields (for example `scope`), which is how the release asset verification failed.

## [0.2.0] - 2026-10-15

### Added

- **`allowArchived` on promotion.** `mem_promote` and
  `POST /api/observations/{id}/promote` accept `allowArchived=true` as the explicit
  opt-in to promote a retired (`archived`) row.

### Changed

- **Promotion never destroys live global truth with retired content.** Promoting an
  `archived` row is now refused by default (`outcome=refused`,
  `reason=source_archived`): the HTTP route answers `409 Conflict` and the MCP tool
  returns an `isError` result the model can act on. With `allowArchived=true` the
  promotion proceeds, but the surviving global row adopts the source's lifecycle
  state, so retired content lands `archived` instead of silently becoming active. A
  live teaching still revives an archived target to the source's state.
- **Slash topic keys are retrievable over HTTP.** The canonical by-key lookup is now
  the query form `GET /api/observations/topic?topicKey=svc/auth`; the path form is
  kept for plain keys and as a catch-all route, and a still-encoded `%2F` is decoded,
  so keys like `architecture/auth-model` no longer come back as a silent empty list.
- **`mem_delete` reaches global memory.** The extension's `mem_delete` accepts
  `scope`, and the server DELETE route resolves `scope=global` read-only (a miss
  never materialises the sentinel).

### Fixed

- **Bounded model-facing reads in the Pi extension.** `mem_get_observation`,
  `mem_search` and `mem_timeline` (and the other read tools) now cap every text
  field at 2000 characters with an explicit `…`, and bound the structured `details`
  payloads to match the MCP server caps; `mem_timeline` is also hard-capped at 50
  rows per key. Previously a single 6000-character body reached the model intact.


Hardening of the memory engine plus the features the product actually needed:
memory scopes, a cheap review/lifecycle loop, and a portable MCP surface.

### Added

- **Global memory scope.** A memory can be written to the reserved `_global`
  namespace (`scope=global`) and is then readable from every project. It shares
  the same store, FTS index and upsert-by-`topic_key` machinery as a project
  namespace; reads resolve it read-only, so a project that never wrote global
  memory does not create it.
- **Promotion.** `hannigram promote <id>` / `POST /api/observations/{id}/promote`
  moves a project memory into the global scope when a lesson turned out to be
  generic. When the `topic_key` already exists globally the promoted content wins
  and the global row is updated in place instead of accumulating duplicates.
- **Cross-scope search.** `mem_search` / `/api/search` / `hannigram search` accept
  `scope=project|global|all` (default `all`) and label every hit with its origin,
  so the caller can tell a repo fact from a universal heuristic.
- **Observation lifecycle.** `state` (`active` / `needs_review` / `archived`) and
  `pinned` on every observation. Reads return `active` rows only; `archived` is
  the safe forget and is excluded unless explicitly requested. Pinned rows sort
  first.
- **Review loop.** A save returns a compact deterministic `review` hint when it
  overwrote a `topic_key` or matched a near-duplicate in the same scope, and
  `mem_review` / `GET /api/review` lists `needs_review` rows plus duplicate
  candidates with truncated previews and a hard cap.
- **`mem_update`.** Edit an observation by id (`PUT /api/observations/{id}`,
  `hannigram update <id>`): omitted fields are left unchanged, an empty string
  clears one, `created_at_utc` is preserved and `updated_at_utc` moves. This
  closes the `mem_update` contract the sODD skill already referenced.
- **Bounded context surface.** `GET /api/context` returns at most 3 pinned global
  heuristics plus 6 recent project rows, previews only, with the caps as store
  constants and no parameter that could widen them.
- **stdio MCP server.** New `src/HanniGram.Mcp` (`hannigram-mcp.exe`) speaks
  JSON-RPC 2.0 over stdin/stdout, reuses `HanniGram.Core` directly (no daemon
  dependency) and exposes the same 14 `mem_*` tools. It is opt-in and
  disconnected by default, and it coexists with the running daemon on the same
  database. Shipped in the Windows release asset.
- **Pi extension surface.** The extension now registers `mem_update`,
  `mem_review`, `mem_promote`, `mem_state` and `mem_pin` (14 tools in total),
  accepts `scope` on `mem_save`/`mem_search`, and surfaces the save-time review
  hint and the origin scope of every search hit. All tools stay on demand: the
  extension never injects memory text into the prompt on its own.
- **Test projects.** `tests/HanniGram.Core.Tests` (xUnit, all store tests against
  a temp database) and `tests/HanniGram.Mcp.Tests`, wired into
  `.github/workflows/ci.yml`.

### Changed

- **FTS indexing is trigger-based.** `observations_fts` is synchronized by
  `AFTER INSERT` / `AFTER DELETE` / `AFTER UPDATE` triggers using the FTS5
  `'delete'` command, instead of re-indexing the whole corpus on every write.
- **`schema` version bumps to 4**; existing databases migrate in place
  (`state`/`pinned` columns, the sessions uniqueness index and the FTS
  re-index), and both the migrating and the already-migrated path re-assert the
  indexes and triggers they need.
- **CLI.** `save`/`search` accept `--scope`, and `update`, `state`, `pin` and
  `review` were added; `search` prints each hit with its origin scope.
- **Release asset** now also contains `hannigram-mcp.exe`, with a stdio smoke
  test next to the daemon one.
- **Local memory export.** `hannigram init` no longer writes the machine's
  absolute database path into `.hannigram/memory.md`: the file names the default
  database location and its environment variable instead, so a committed export
  is portable.
- **Pi extension package version** is now `0.2.0`.

### Fixed

- **Empty/short search queries crashed the daemon.** `q=ab`, `q=a` and `q=` used
  to return HTTP 500 (`fts5: syntax error near ""`) because `MATCH ''` was
  issued; they now return `200 []`. The daemon also returns a JSON error envelope
  instead of a bodiless 500.
- **`what` / `why` / `where` / `learned` were never searchable** — they are now
  part of the FTS index.
- **Recency ignored updates.** Ordering now uses `updated_at_utc` (with pinned
  rows first), so an upserted observation moves to the front instead of staying
  buried at its original position.
- **`limit` was unvalidated.** Caller-supplied limits are clamped on every
  read path, so `limit=-1` can no longer dump the whole corpus from the local
  API.
- **Sessions duplicated.** `(project_id, session_id)` is unique (after
  deduplicating legacy rows), `StartSession` is idempotent and
  `mem_session_summary` no longer accumulates rows or overwrites its own
  summary.
- **Trigram fallback matched across tokens.** The fuzzy fallback now builds
  trigrams per token and ANDs the per-token OR-groups, so a typo inside a token
  still matches while an absent token can no longer be satisfied by a different
  word.
- **CLI ignored `HANNIGRAM_CWD`** and the positional `project [cwd]` argument;
  both now resolve with documented precedence.
- **Test suite flake.** The suite is serialized, which removes an intermittent
  `ObjectDisposedException` caused by `ClearAllPools()` racing other test
  classes.

## [0.1.0] - 2026-10-15

First public release: a persistent, per-project memory engine for coding agents,
integrated natively with HanniPI through a Pi extension (no MCP).

### Added

- **Memory engine** (`.NET 10`, SQLite + FTS5): structured observations
  (What/Why/Where/Learned) with title, type and stable `topic_key`, strictly
  scoped per project and resolved from the working directory (git remote when
  available).
- **HTTP daemon** (`HanniGram.Server`, loopback by default) and a standalone
  **CLI** (`hannigram`) that needs no daemon.
- **FTS5 trigram search** with typo tolerance, substring and CJK support, plus a
  fuzzy trigram retry ranked by bm25 when the strict match is empty.
- **Upsert by `topic_key`**: saving an existing key updates the observation
  instead of duplicating it.
- **ADR / decisions**: `type=adr` observations with `topic_key=adr/<slug>` hold
  the conventions the agent must respect.
- **Sessions and handoff**: `session_summary` records goal, discoveries and next
  steps for the next session.
- **Native Pi extension** registering the `mem_*` tools, which also starts the
  daemon when it is not answering `/health`.
- **`hannigram init`** creates the project in the database and writes the local
  `.hannigram/memory.md` export so a file-only harness can read the memory
  without HanniGram.
- **`mem_delete`** tool plus the matching delete endpoint.
- **Windows release asset** (`hannigram-windows-x64.zip`) with daemon + CLI +
  Pi extension, built by `scripts/pack-asset.ps1` and published from a `v*` tag
  by `.github/workflows/release.yml` with a `/health` smoke test.

[Unreleased]: https://github.com/AlPeFe/HanniGram/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/AlPeFe/HanniGram/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/AlPeFe/HanniGram/releases/tag/v0.1.0
