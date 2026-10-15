/**
 * HanniGram — native Pi extension.
 *
 * Registers the mem_* tools that talk to the HanniGram daemon over HTTP
 * (default http://127.0.0.1:8765). No MCP: the harness consumes HanniGram
 * natively as tooling, exactly like any built-in Pi tool.
 *
 * The daemon resolves the project from the session cwd (git remote when
 * available), so memory is scoped per project automatically.
 *
 * HanniGram lives as long as Hanni: on load, and before every mem_* call, the
 * extension ensures the daemon process is up (health check on /health; if the
 * daemon is not reachable it spawns it detached). Override the daemon path with
 * HANNIGRAM_DAEMON and the endpoint with HANNIGRAM_URL / HANNIGRAM_URLS.
 */

import type { ExtensionAPI, ExtensionToolContext } from "@earendil-works/pi-coding-agent";
import { Type } from "typebox";
import { spawn } from "node:child_process";
import * as path from "node:path";
import * as fs from "node:fs";
import { fileURLToPath } from "node:url";

const DEFAULT_BASE = "http://127.0.0.1:8765";
const DEFAULT_URLS = "http://127.0.0.1:8765";
const HEALTH_TIMEOUT_MS = 400;
const READY_POLL_MS = 500;
const READY_POLLS = 20;
const UP_CACHE_TTL_MS = 15_000;

function baseUrl(): string {
	return process.env.HANNIGRAM_URL ?? DEFAULT_BASE;
}

/** Small HTTP helper. Throws with the daemon's error message on failure. */
async function api<T>(path: string, init?: RequestInit): Promise<T> {
	const res = await fetch(`${baseUrl()}${path}`, {
		...init,
		headers: { "content-type": "application/json", ...(init?.headers ?? {}) },
	});
	if (!res.ok) {
		const body = await res.text().catch(() => "");
		throw new Error(`HanniGram ${res.status}: ${body || res.statusText}`);
	}
	return (await res.json()) as T;
}

/** Like api(), but a 404 comes back as null so a missing row is a result, not an error. */
async function apiOrMissing<T>(path: string, init?: RequestInit): Promise<T | null> {
	const res = await fetch(`${baseUrl()}${path}`, {
		...init,
		headers: { "content-type": "application/json", ...(init?.headers ?? {}) },
	});
	if (res.status === 404) return null;
	if (!res.ok) {
		const body = await res.text().catch(() => "");
		throw new Error(`HanniGram ${res.status}: ${body || res.statusText}`);
	}
	return (await res.json()) as T;
}

/** Build the query string carrying the project scope from the session cwd. */
function scope(cwd: string, extra: Record<string, string> = {}): string {
	const p = new URLSearchParams({ cwd, ...extra });
	return p.toString();
}

// Model-facing caps, matching the MCP server's budget (src/HanniGram.Mcp/Bounded.cs):
// no tool may hand the model an unbounded memory body. Truncation is marked with an
// explicit ellipsis so it is obvious that content was cut.
const MAX_FIELD_CHARS = 2000;
const MAX_TITLE_CHARS = 200;
const MAX_TOKEN_CHARS = 200;
const TRUNCATION_MARKER = "\u2026";

/** Collapse whitespace and truncate to `max` characters, marking any truncation. */
function bound(text: unknown, max = MAX_FIELD_CHARS): string {
	if (typeof text !== "string" || text.length === 0) return "";
	const collapsed = text.replace(/\s+/g, " ").trim();
	return collapsed.length <= max ? collapsed : collapsed.slice(0, max) + TRUNCATION_MARKER;
}

const CONTENT_FIELDS = ["content", "what", "why", "where", "learned"] as const;
const TOKEN_FIELDS = ["topicKey", "type"] as const;

/**
 * A copy of a memory row with every free-text field bounded, so the structured
 * `details` payload is bounded exactly like the rendered text. Non-string fields
 * (ids, flags) pass through untouched.
 */
function boundedObservation<T>(row: T): T {
	const copy: Record<string, unknown> = { ...(row as Record<string, unknown>) };
	if (typeof copy.title === "string") copy.title = bound(copy.title, MAX_TITLE_CHARS);
	for (const field of CONTENT_FIELDS) if (typeof copy[field] === "string") copy[field] = bound(copy[field]);
	for (const field of TOKEN_FIELDS) if (typeof copy[field] === "string") copy[field] = bound(copy[field], MAX_TOKEN_CHARS);
	return copy as T;
}

function healthUrl(): string {
	return `${baseUrl()}/health`;
}

// Directory of this extension (loaded from source via jiti, so import.meta.url
// points at the real checkout path).
const extensionDir = fileURLToPath(new URL(".", import.meta.url));

/** Resolve the HanniGram.Server executable (env override first, else dev checkout). */
function daemonPath(): string | null {
	const envBin = process.env.HANNIGRAM_DAEMON;
	if (envBin) return fs.existsSync(envBin) ? envBin : null;
	const root = path.resolve(extensionDir, "..");
	const exeName = process.platform === "win32" ? "HanniGram.Server.exe" : "HanniGram.Server";
	for (const cfg of ["Release", "Debug"]) {
		const p = path.join(root, "src", "HanniGram.Server", "bin", cfg, "net10.0", exeName);
		if (fs.existsSync(p)) return p;
	}
	return null;
}

/** True if the daemon answers /health. */
function daemonUp(): Promise<boolean> {
	const ctl = new AbortController();
	const timer = setTimeout(() => ctl.abort(), HEALTH_TIMEOUT_MS);
	return fetch(healthUrl(), { signal: ctl.signal })
		.then((r) => r.ok)
		.catch(() => false)
		.finally(() => clearTimeout(timer));
}

/** Spawn the daemon detached so it survives this Hanni session. Returns true if spawned. */
function startDaemon(): boolean {
	const bin = daemonPath();
	if (!bin) return false;
	try {
		const child = spawn(bin, ["--urls", process.env.HANNIGRAM_URLS ?? DEFAULT_URLS], {
			detached: true,
			stdio: "ignore",
			windowsHide: true,
		});
		child.unref();
		return true;
	} catch {
		return false;
	}
}

let lastUpAt = 0;

/** Ensure the daemon is reachable, starting it if not. Cheap when already up. */
async function ensureDaemon(): Promise<void> {
	if (Date.now() - lastUpAt < UP_CACHE_TTL_MS) return;
	if (await daemonUp()) {
		lastUpAt = Date.now();
		return;
	}
	if (startDaemon()) {
		for (let i = 0; i < READY_POLLS; i++) {
			if (await daemonUp()) {
				lastUpAt = Date.now();
				return;
			}
			await new Promise((r) => setTimeout(r, READY_POLL_MS));
		}
	}
}

export default function (pi: ExtensionAPI) {
	// Hanni owns HanniGram: bring the daemon up as soon as the extension loads.
	void ensureDaemon();

	// ------------------------------------------------------------------
	// mem_current_project — confirm the resolved project
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_current_project",
		label: "HanniGram: current project",
		description:
			"Confirm the HanniGram project resolved for the current working directory (git remote when available). Call this first to orient before reading or writing memory.",
		parameters: Type.Object({}),
		execute: async (_id, _params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			await ensureDaemon();
			const p = await api<{ id: number; name: string; rootPath?: string }>(
				`/api/projects/current?${scope(ctx.cwd)}`,
			);
			return {
				content: [{ type: "text", text: `Project: ${p.name}${p.rootPath ? ` (${p.rootPath})` : ""}` }],
				details: p,
			};
		},
	});

	// ------------------------------------------------------------------
	// mem_save — save a durable observation
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_save",
		label: "HanniGram: save memory",
		description:
			"Save a durable, structured memory observation for the CURRENT project (scoped by git remote). Two kinds of memory: (1) decisions & conventions the agent must RESPECT — set type='adr' so they are always surfaced to the agent; (2) context/ideas in progress — discoveries, patterns, bugfixes, config, concepts being worked on (any other type). Set scope='global' only for a heuristic that repeats across many projects. Capture durable knowledge, never raw tool output or every turn. Give a short searchable title.",
		parameters: Type.Object({
			title: Type.String({ description: "Short, searchable title of the observation" }),
			content: Type.String({ description: "The full observation content" }),
			what: Type.Optional(Type.String({ description: "What was done" })),
			why: Type.Optional(Type.String({ description: "Why it was done" })),
			where: Type.Optional(Type.String({ description: "Where (file/module) it applies" })),
			learned: Type.Optional(Type.String({ description: "The lesson or takeaway" })),
			topicKey: Type.Optional(
				Type.String({ description: "Stable topic key, e.g. 'architecture/auth-model'. Reuse to update an evolving topic." }),
			),
			type: Type.Optional(
				Type.String({ description: "Observation type. Two buckets per project: 'adr' = decision/convention the agent must respect (always surfaced); anything else ('discovery', 'idea', 'pattern', 'bugfix', ...) = context/idea being worked on." }),
			),
			scope: Type.Optional(
				Type.String({ description: "Where to write: 'project' (default, this repo) or 'global' (shared by every project)." }),
			),
		}),
		execute: async (_id, params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			await ensureDaemon();
			const res = await api<{
				observation: { id: number; title: string; topicKey?: string };
				created: boolean;
				scope: string;
				review?: { kind: string; id: number; title: string };
			}>(`/api/observations?${scope(ctx.cwd)}`, {
				method: "POST",
				body: JSON.stringify(params),
			});
			const { observation: obs, created, review } = res;
			ctx.ui.notify(
				created
					? `✓ Memoria guardada #${obs.id}: ${obs.title}${obs.topicKey ? ` [${obs.topicKey}]` : ""}`
					: `✎ Memoria actualizada #${obs.id}: ${obs.title}${obs.topicKey ? ` [${obs.topicKey}]` : ""}`,
				"info",
			);
			// The daemon's compact review hint (a few tokens, never a body) tells the
			// model the save collided with something it may now want to review.
			const hint = review ? `\nreview: ${review.kind} #${review.id} ${review.title}` : "";
			return {
				content: [
					{
						type: "text",
						text: `${created ? "Saved" : "Updated"} observation #${obs.id} (${res.scope}): ${obs.title}${obs.topicKey ? ` [topic: ${obs.topicKey}]` : ""}${hint}`,
					},
				],
				details: obs,
			};
		},
	});

	// ------------------------------------------------------------------
	// mem_delete — delete an observation
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_delete",
		label: "HanniGram: delete memory",
		description:
			"Delete a memory observation by id from the current project. Use to remove stale, wrong, or superseded observations. Returns whether it was deleted.",
		parameters: Type.Object({
			id: Type.Number({ description: "Observation id to delete" }),
		}),
		execute: async (_id, params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			await ensureDaemon();
			const res = await fetch(`${baseUrl()}/api/observations/${params.id}?${scope(ctx.cwd)}`, {
				method: "DELETE",
			});
			if (res.status === 404) {
				ctx.ui.notify(`✗ Memoria #${params.id} no encontrada`, "warning");
				return { content: [{ type: "text", text: `Observation #${params.id} not found.` }], details: { deleted: false } };
			}
			if (!res.ok) {
				const body = await res.text().catch(() => "");
				throw new Error(`HanniGram ${res.status}: ${body || res.statusText}`);
			}
			ctx.ui.notify(`✓ Memoria eliminada #${params.id}`, "info");
			return { content: [{ type: "text", text: `Deleted observation #${params.id}.` }], details: { deleted: true, id: params.id } };
		},
	});

	// ------------------------------------------------------------------
	// mem_search — full-text search over project memory
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_search",
		label: "HanniGram: search memory",
		description:
			"Full-text search over memory (SQLite FTS5). scope='all' (default) covers the project and the shared global memory, and every hit shows its origin. Use before revisiting a decision, bug, convention, or request that may already be known. Results are previews, not the complete record.",
		parameters: Type.Object({
			query: Type.String({ description: "Search terms; all terms must match" }),
			limit: Type.Optional(Type.Number({ description: "Max results (default 10)" })),
			type: Type.Optional(Type.String({ description: "Filter by observation type (e.g. 'adr', 'decision', 'bugfix', 'pattern')" })),
			topicKey: Type.Optional(Type.String({ description: "Restrict to a single topic key, e.g. 'ui/palette'" })),
			scope: Type.Optional(
				Type.String({ description: "Namespaces to search: 'project', 'global', or 'all' (default). Each hit is labelled with its origin." }),
			),
		}),
		execute: async (_id, params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			await ensureDaemon();
			const hits = await api<
				Array<{ id: number; title: string; content: string; topicKey?: string; type?: string; scope?: string }>
			>(
				`/api/search?${scope(ctx.cwd, {
					q: params.query,
					limit: String(params.limit ?? 10),
					scope: params.scope ?? "all",
					...(params.type ? { type: params.type } : {}),
					...(params.topicKey ? { topicKey: params.topicKey } : {}),
				})}`,
			);
			if (hits.length === 0) {
				return { content: [{ type: "text", text: "No memory matches." }], details: [] };
			}
			const text = hits
				.map((h) => `#${h.id} (${h.scope ?? "project"}) ${h.title}${h.topicKey ? ` [${h.topicKey}]` : ""}\n  ${h.content}`)
				.join("\n");
			return { content: [{ type: "text", text }], details: hits };
		},
	});

	// ------------------------------------------------------------------
	// mem_context — recent observations for the project
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_context",
		label: "HanniGram: recent memory context",
		description:
			"Return the most recent observations for the current project. Use at the start of related work to recover relevant history before continuing.",
		parameters: Type.Object({
			limit: Type.Optional(Type.Number({ description: "Max observations (default 20)" })),
		}),
		execute: async (_id, params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			await ensureDaemon();
			const obs = await api<Array<{ id: number; title: string; content: string; topicKey?: string; type?: string }>>(
				`/api/observations?${scope(ctx.cwd, { limit: String(params.limit ?? 20) })}`,
			);
			if (obs.length === 0) {
				return { content: [{ type: "text", text: "No memory yet for this project." }], details: [] };
			}
			const text = obs
				.map((o) => `#${o.id} ${o.title}${o.topicKey ? ` [${o.topicKey}]` : ""}\n  ${o.content}`)
				.join("\n");
			return { content: [{ type: "text", text }], details: obs };
		},
	});

	// ------------------------------------------------------------------
	// mem_timeline — observations by topic key
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_timeline",
		label: "HanniGram: memory timeline by topic",
		description:
			"Return all observations for a given topic key in the current project, newest first. Use when surrounding session context matters for an evolving topic.",
		parameters: Type.Object({
			topicKey: Type.String({ description: "The topic key to retrieve, e.g. 'architecture/auth-model'" }),
		}),
		execute: async (_id, params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			await ensureDaemon();
			const obs = await api<Array<{ id: number; title: string; content: string; topicKey?: string }>>(
				`/api/observations/topic/${encodeURIComponent(params.topicKey)}?${scope(ctx.cwd)}`,
			);
			if (obs.length === 0) {
				return { content: [{ type: "text", text: `No observations for topic '${params.topicKey}'.` }], details: [] };
			}
			const text = obs.map((o) => `#${o.id} ${o.title}\n  ${o.content}`).join("\n");
			return { content: [{ type: "text", text }], details: obs };
		},
	});

	// ------------------------------------------------------------------
	// mem_get_observation — full detail of one observation
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_get_observation",
		label: "HanniGram: get observation",
		description: "Return the full detail of a single observation by id, before relying on it.",
		parameters: Type.Object({
			id: Type.Number({ description: "Observation id" }),
		}),
		execute: async (_id, params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			await ensureDaemon();
			const obs = await api<Record<string, unknown>>(`/api/observations/${params.id}?${scope(ctx.cwd)}`);
			return { content: [{ type: "text", text: JSON.stringify(obs, null, 2) }], details: obs };
		},
	});

	// ------------------------------------------------------------------
	// mem_suggest_topic_key — existing topic keys
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_suggest_topic_key",
		label: "HanniGram: suggest topic keys",
		description:
			"List existing topic keys in the current project. Use when unsure of the stable topic_key to reuse for an evolving topic.",
		parameters: Type.Object({}),
		execute: async (_id, _params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			await ensureDaemon();
			const topics = await api<string[]>(`/api/topics?${scope(ctx.cwd)}`);
			if (topics.length === 0) {
				return { content: [{ type: "text", text: "No topic keys yet." }], details: [] };
			}
			return { content: [{ type: "text", text: topics.join("\n") }], details: topics };
		},
	});

	// ------------------------------------------------------------------
	// mem_session_summary — handoff at session end
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_session_summary",
		label: "HanniGram: session summary",
		description:
			"Save a session handoff summary for the current project: goal, discoveries, accomplished work, next steps. Call before ending a session so the next session can recover context.",
		parameters: Type.Object({
			summary: Type.String({ description: "Summary of what was accomplished and discovered" }),
			goal: Type.Optional(Type.String({ description: "The session goal" })),
			nextSteps: Type.Optional(Type.String({ description: "Next steps for the following session" })),
			sessionId: Type.Optional(Type.String({ description: "Session id; a new one is created if omitted" })),
		}),
		execute: async (_id, params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			await ensureDaemon();
			const sessionId = params.sessionId ?? `pi-${Date.now().toString(36)}`;
			await api(`/api/sessions/start?${scope(ctx.cwd)}`, {
				method: "POST",
				body: JSON.stringify({ sessionId }),
			});
			await api(`/api/sessions/end?${scope(ctx.cwd)}`, {
				method: "POST",
				body: JSON.stringify({
					sessionId,
					summary: params.summary,
					goal: params.goal,
					nextSteps: params.nextSteps,
				}),
			});
			ctx.ui.notify(`✓ Resumen de sesión guardado (${sessionId})`, "info");
			return {
				content: [{ type: "text", text: `Session ${sessionId} summary saved.` }],
				details: { sessionId },
			};
		},
	});

	// ------------------------------------------------------------------
	// mem_update — edit an observation in place
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_update",
		label: "HanniGram: update memory",
		description:
			"Edit an existing observation by id. Only the fields you pass change; an empty string clears one. Use it to correct or extend memory instead of saving a near-duplicate.",
		parameters: Type.Object({
			id: Type.Number({ description: "Observation id" }),
			title: Type.Optional(Type.String({ description: "New title" })),
			content: Type.Optional(Type.String({ description: "New content" })),
			what: Type.Optional(Type.String({ description: "New 'what'; empty string clears it" })),
			why: Type.Optional(Type.String({ description: "New 'why'; empty string clears it" })),
			where: Type.Optional(Type.String({ description: "New 'where'; empty string clears it" })),
			learned: Type.Optional(Type.String({ description: "New 'learned'; empty string clears it" })),
			topicKey: Type.Optional(Type.String({ description: "New topic key; empty string clears it" })),
			type: Type.Optional(Type.String({ description: "New type; empty string clears it" })),
			scope: Type.Optional(
				Type.String({ description: "Namespace that owns the row: 'project' (default) or 'global'." }),
			),
		}),
		execute: async (_id, params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			await ensureDaemon();
			const { id, scope: rowScope, ...fields } = params;
			const obs = await apiOrMissing<{ id: number; title: string; state?: string }>(
				`/api/observations/${id}?${scope(ctx.cwd, rowScope ? { scope: rowScope } : {})}`,
				{ method: "PUT", body: JSON.stringify(fields) },
			);
			if (!obs) {
				ctx.ui.notify(`✗ Memoria #${id} no encontrada`, "warning");
				return { content: [{ type: "text", text: `Observation #${id} not found.` }], details: { updated: false } };
			}
			ctx.ui.notify(`✎ Memoria actualizada #${obs.id}: ${obs.title}`, "info");
			return { content: [{ type: "text", text: `Updated observation #${obs.id}: ${obs.title}` }], details: obs };
		},
	});

	// ------------------------------------------------------------------
	// mem_review — bounded review listing
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_review",
		label: "HanniGram: review memory",
		description:
			"List memory that needs attention: rows flagged needs_review plus near-duplicate candidates, with truncated previews. Follow up with mem_update, mem_state or mem_delete.",
		parameters: Type.Object({
			limit: Type.Optional(Type.Number({ description: "Max items (default 20)" })),
			scope: Type.Optional(
				Type.String({ description: "Namespaces to review: 'project', 'global', or 'all' (default)." }),
			),
		}),
		execute: async (_id, params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			await ensureDaemon();
			const items = await api<
				Array<{ kind: string; id: number; scope?: string; title: string; preview: string; duplicateOfId?: number }>
			>(
				`/api/review?${scope(ctx.cwd, {
					limit: String(params.limit ?? 20),
					...(params.scope ? { scope: params.scope } : {}),
				})}`,
			);
			if (items.length === 0) {
				return { content: [{ type: "text", text: "Nothing to review." }], details: [] };
			}
			const text = items
				.map(
					(i) =>
						`[${i.kind}] #${i.id} (${i.scope ?? "project"}) ${i.title}`
						+ (i.duplicateOfId ? ` ~ duplicate of #${i.duplicateOfId}` : "")
						+ `\n  ${i.preview}`,
				)
				.join("\n");
			return { content: [{ type: "text", text }], details: items };
		},
	});

	// ------------------------------------------------------------------
	// mem_promote — promote a project memory to the global scope
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_promote",
		label: "HanniGram: promote memory",
		description:
			"Promote a project observation to the shared global memory when the lesson turned out to repeat across projects. If the topic key already exists globally, the promoted content overwrites that row. An archived row is refused unless allowArchived=true (the promoted row then stays archived).",
		parameters: Type.Object({
			id: Type.Number({ description: "Observation id in the current project" }),
			allowArchived: Type.Optional(
				Type.Boolean({ description: "Promote even when the row is archived. The promoted row stays archived; default false refuses." }),
			),
		}),
		execute: async (_id, params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			await ensureDaemon();
			const query = scope(ctx.cwd, params.allowArchived ? { allowArchived: "true" } : {});
			const res = await fetch(`${baseUrl()}/api/observations/${params.id}/promote?${query}`, { method: "POST" });
			if (res.status === 404) {
				ctx.ui.notify(`✗ Memoria #${params.id} no encontrada`, "warning");
				return { content: [{ type: "text", text: `Observation #${params.id} not found.` }], details: { promoted: false } };
			}
			const body = (await res.json().catch(() => ({}))) as {
				observation?: { id: number; title: string };
				merged?: boolean;
				outcome?: string;
				reason?: string;
			};
			if (res.status === 409) {
				// A refusal is a result the model can act on (retry with allowArchived),
				// not a thrown error.
				ctx.ui.notify(`✗ Memoria #${params.id} archivada: promoción rechazada`, "warning");
				return {
					content: [
						{
							type: "text",
							text: `Promotion refused (${body.reason ?? "refused"}): observation #${params.id} is archived. Pass allowArchived=true to promote it anyway; the promoted row stays archived.`,
						},
					],
					details: body,
				};
			}
			if (!res.ok) {
				throw new Error(`HanniGram ${res.status}: ${JSON.stringify(body) || res.statusText}`);
			}
			const promoted = body.observation!;
			ctx.ui.notify(`✓ Memoria promovida #${promoted.id} → global (${body.outcome})`, "info");
			return {
				content: [
					{
						type: "text",
						text: `Promoted #${promoted.id} to global (${body.outcome}): ${bound(promoted.title, MAX_TITLE_CHARS)}`,
					},
				],
				details: body,
			};
		},
	});

	// ------------------------------------------------------------------
	// mem_state — lifecycle state
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_state",
		label: "HanniGram: set lifecycle state",
		description:
			"Set an observation's lifecycle state: 'active', 'needs_review' or 'archived'. Archiving is the safe forget — the row is kept but excluded from default reads.",
		parameters: Type.Object({
			id: Type.Number({ description: "Observation id" }),
			state: Type.String({ description: "New state: 'active', 'needs_review' or 'archived'" }),
			scope: Type.Optional(
				Type.String({ description: "Namespace that owns the row: 'project' (default) or 'global'." }),
			),
		}),
		execute: async (_id, params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			await ensureDaemon();
			const obs = await apiOrMissing<{ id: number; title: string; state: string }>(
				`/api/observations/${params.id}/state?${scope(ctx.cwd, params.scope ? { scope: params.scope } : {})}`,
				{ method: "POST", body: JSON.stringify({ state: params.state }) },
			);
			if (!obs) {
				ctx.ui.notify(`✗ Memoria #${params.id} no encontrada o estado inválido`, "warning");
				return { content: [{ type: "text", text: `Observation #${params.id} not found.` }], details: { updated: false } };
			}
			ctx.ui.notify(`✓ Memoria #${obs.id} → ${obs.state}`, "info");
			return { content: [{ type: "text", text: `Observation #${obs.id} state: ${obs.state}` }], details: obs };
		},
	});

	// ------------------------------------------------------------------
	// mem_pin — pin / unpin an observation
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_pin",
		label: "HanniGram: pin memory",
		description:
			"Pin or unpin an observation. Pinned global heuristics are the only global memory that enters the automatic context budget (at most 3).",
		parameters: Type.Object({
			id: Type.Number({ description: "Observation id" }),
			pinned: Type.Boolean({ description: "true to pin, false to unpin" }),
			scope: Type.Optional(
				Type.String({ description: "Namespace that owns the row: 'project' (default) or 'global'." }),
			),
		}),
		execute: async (_id, params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			await ensureDaemon();
			const obs = await apiOrMissing<{ id: number; title: string; pinned: boolean }>(
				`/api/observations/${params.id}/pin?${scope(ctx.cwd, params.scope ? { scope: params.scope } : {})}`,
				{ method: "POST", body: JSON.stringify({ pinned: params.pinned }) },
			);
			if (!obs) {
				ctx.ui.notify(`✗ Memoria #${params.id} no encontrada`, "warning");
				return { content: [{ type: "text", text: `Observation #${params.id} not found.` }], details: { updated: false } };
			}
			ctx.ui.notify(`✓ Memoria #${obs.id} pinned=${obs.pinned}`, "info");
			return { content: [{ type: "text", text: `Observation #${obs.id} pinned: ${obs.pinned}` }], details: obs };
		},
	});
}
