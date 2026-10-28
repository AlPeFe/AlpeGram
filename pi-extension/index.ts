/**
 * HanniGram — native Pi extension.
 *
 * Registers the four mem_* tools that talk to the HanniGram daemon over HTTP
 * (default http://127.0.0.1:8765). No MCP: the harness consumes HanniGram
 * natively as tooling, exactly like any built-in Pi tool.
 *
 * The surface is deliberately four tools (SPEC-002): the memory layer used to
 * cost 1,734 tokens of schema per turn across 14 tools, more than the whole
 * harness. Consolidated into mem_search / mem_save / mem_get / mem_admin with
 * terse descriptions and action/kind enums, the exact same behaviour costs
 * ~680. Read paths are mem_get, writes are mem_save, maintenance is mem_admin.
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

/**
 * Endpoint del daemon. `HANNIGRAM_URL` manda si está puesto; si no, se usa la primera URL de
 * `HANNIGRAM_URLS` (la misma variable con la que se arranca el daemon), y solo entonces el default.
 * Sin esto, un daemon en otro puerto se ignoraba y las escrituras caían en el de siempre.
 */
function baseUrl(): string {
	const explicit = process.env.HANNIGRAM_URL?.trim();
	if (explicit) return explicit.replace(/\/+$/, "");
	const urls = process.env.HANNIGRAM_URLS;
	if (urls) {
		const first = urls
			.split(/[;,]/)
			.map((url) => url.trim())
			.find(Boolean);
		if (first) return first.replace(/\/+$/, "");
	}
	return DEFAULT_BASE;
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

/** Heading line shared by every list rendering: `#12 [topic] Title`. */
function heading(row: { id: number; title?: string; topicKey?: string; scope?: string }): string {
	const origin = row.scope ? ` (${row.scope})` : "";
	const topic = row.topicKey ? ` [${bound(row.topicKey, MAX_TOKEN_CHARS)}]` : "";
	return `#${row.id}${origin} ${bound(row.title, MAX_TITLE_CHARS)}${topic}`;
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

/** Fields `mem_admin action=update` may patch (anything else is ignored). */
const UPDATE_FIELDS = ["title", "content", "what", "why", "where", "learned", "topicKey", "type"] as const;

/** Keep only the known, non-undefined update fields; "" still clears a field. */
function pickFields(raw: Record<string, string> | undefined): Record<string, string> {
	const out: Record<string, string> = {};
	if (!raw) return out;
	for (const field of UPDATE_FIELDS) {
		const value = raw[field];
		if (typeof value === "string") out[field] = value;
	}
	return out;
}

/** Shorthand result: one text part, details echoed back for callers. */
function result(text: string, details: unknown) {
	return { content: [{ type: "text" as const, text }], details };
}

export default function (pi: ExtensionAPI) {
	// Hanni owns HanniGram: bring the daemon up as soon as the extension loads.
	void ensureDaemon();

	// ------------------------------------------------------------------
	// mem_search — the entry point for reading memory
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_search",
		label: "HanniGram: search memory",
		description: "Search project and global memory; hits are previews.",
		parameters: Type.Object({
			query: Type.String({ description: "Terms; bm25-ranked, not all required" }),
			limit: Type.Optional(Type.Number({ description: "Default 10" })),
			type: Type.Optional(Type.String({ description: "adr | discovery | pattern | bugfix" })),
			topicKey: Type.Optional(Type.String({ description: "One topic key" })),
			scope: Type.Optional(
				Type.String({ description: "Namespaces (default all)", enum: ["project", "global", "all"] }),
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
			if (hits.length === 0) return result("No memory matches.", []);
			const text = hits.map((h) => `${heading(h)}\n  ${bound(h.content)}`).join("\n");
			return result(text, hits.map(boundedObservation));
		},
	});

	// ------------------------------------------------------------------
	// mem_save — the entry point for writing memory
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_save",
		label: "HanniGram: save memory",
		description: "Save a memory; reusing a topicKey updates that topic's row.",
		parameters: Type.Object({
			title: Type.String({ description: "Short, searchable title" }),
			content: Type.String({ description: "The observation" }),
			what: Type.Optional(Type.String({ description: "What was done" })),
			why: Type.Optional(Type.String({ description: "Why" })),
			where: Type.Optional(Type.String({ description: "File or module" })),
			learned: Type.Optional(Type.String({ description: "Takeaway" })),
			topicKey: Type.Optional(Type.String({ description: "Stable key, e.g. architecture/auth-model" })),
			type: Type.Optional(
				Type.String({ description: "adr = always-surfaced decision; else discovery | pattern | bugfix" }),
			),
			scope: Type.Optional(Type.String({ description: "Default project", enum: ["project", "global"] })),
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
			return result(
				`${created ? "Saved" : "Updated"} observation #${obs.id} (${res.scope}): ${bound(obs.title, MAX_TITLE_CHARS)}${obs.topicKey ? ` [topic: ${bound(obs.topicKey, MAX_TOKEN_CHARS)}]` : ""}${hint}`,
				boundedObservation(obs),
			);
		},
	});

	// ------------------------------------------------------------------
	// mem_get — every read path, selected by kind
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_get",
		label: "HanniGram: read memory",
		description: "Read memory: a row, recent context, a topic, or the project.",
		parameters: Type.Object({
			kind: Type.String({ description: "Default id", enum: ["id", "context", "topic", "project"] }),
			id: Type.Optional(Type.Number({ description: "kind=id" })),
			topicKey: Type.Optional(Type.String({ description: "kind=topic" })),
			limit: Type.Optional(Type.Number({ description: "kind=context cap (default 20)" })),
		}),
		execute: async (_id, params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			await ensureDaemon();
			const kind = params.kind ?? "id";

			if (kind === "project") {
				const p = await api<{ id: number; name: string; rootPath?: string }>(`/api/projects/current?${scope(ctx.cwd)}`);
				return result(`Project: ${p.name}${p.rootPath ? ` (${p.rootPath})` : ""}`, p);
			}

			if (kind === "id") {
				if (params.id === undefined) return result("kind=id needs an id.", {});
				const obs = await apiOrMissing<Record<string, unknown>>(`/api/observations/${params.id}?${scope(ctx.cwd)}`);
				if (!obs) return result(`Observation #${params.id} not found.`, { found: false });
				const bounded = boundedObservation(obs);
				// Bounded rendering: a raw JSON.stringify used to hand the model a whole
				// 6000-character body. Every field goes through the same budget.
				const lines = [heading(bounded as { id: number })];
				if (bounded.type) lines.push(`type: ${bound(bounded.type, MAX_TOKEN_CHARS)}`);
				if (bounded.state) lines.push(`state: ${bounded.state}`);
				lines.push(bound(bounded.content));
				for (const field of CONTENT_FIELDS) {
					if (field === "content") continue;
					if (bounded[field]) lines.push(`${field}: ${bound(bounded[field])}`);
				}
				return result(lines.join("\n"), bounded);
			}

			if (kind === "topic") {
				if (!params.topicKey) return result("kind=topic needs a topicKey.", []);
				// The canonical by-key lookup is a QUERY parameter: a slash key
				// ("architecture/auth-model") survives URL-encoding exactly, unlike a path
				// segment, which used to come back as a silent empty list.
				const obs = await api<Array<{ id: number; title: string; content: string; topicKey?: string }>>(
					`/api/observations/topic?${scope(ctx.cwd, { topicKey: params.topicKey })}`,
				);
				if (obs.length === 0) return result(`No observations for topic '${params.topicKey}'.`, []);
				const text = obs.map((o) => `${heading(o)}\n  ${bound(o.content)}`).join("\n");
				return result(text, obs.map(boundedObservation));
			}

			const obs = await api<Array<{ id: number; title: string; content: string; topicKey?: string; type?: string }>>(
				`/api/observations?${scope(ctx.cwd, { limit: String(params.limit ?? 20) })}`,
			);
			if (obs.length === 0) return result("No memory yet for this project.", []);
			const text = obs.map((o) => `${heading(o)}\n  ${bound(o.content)}`).join("\n");
			return result(text, obs.map(boundedObservation));
		},
	});

	// ------------------------------------------------------------------
	// mem_admin — maintenance only: nothing here reads a full body
	// ------------------------------------------------------------------
	pi.registerTool({
		name: "mem_admin",
		label: "HanniGram: maintain memory",
		description: "Maintain existing memory: lifecycle, edits, promotion, review, handoff.",
		parameters: Type.Object({
			action: Type.String({
				description: "Operation",
				enum: ["state", "pin", "promote", "update", "delete", "review", "session_summary", "suggest_topic_key"],
			}),
			id: Type.Optional(Type.Number({ description: "state | pin | promote | update | delete" })),
			scope: Type.Optional(Type.String({ description: "Default project", enum: ["project", "global"] })),
			state: Type.Optional(Type.String({ description: "action=state", enum: ["active", "needs_review", "archived"] })),
			pinned: Type.Optional(Type.Boolean({ description: "action=pin" })),
			fields: Type.Optional(
				Type.Record(Type.String(), Type.String(), {
					description: "action=update: title, content, what, why, where, learned, topicKey, type; partial",
				}),
			),
			allowArchived: Type.Optional(Type.Boolean({ description: "action=promote" })),
			summary: Type.Optional(Type.String({ description: "action=session_summary" })),
			goal: Type.Optional(Type.String()),
			nextSteps: Type.Optional(Type.String()),
			sessionId: Type.Optional(Type.String()),
			limit: Type.Optional(Type.Number({ description: "review | suggest_topic_key cap" })),
		}),
		execute: async (_id, params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			await ensureDaemon();
			const { action, id, scope: rowScope } = params;
			const namespaced = (extra: Record<string, string> = {}) => scope(ctx.cwd, rowScope ? { scope: rowScope, ...extra } : extra);

			/** Actions that mutate one existing row need one; a miss is a result, not a throw. */
			const needsId = () => {
				if (id === undefined) return result(`action=${action} needs an id.`, { ok: false });
				return null;
			};
			const missingRow = (verb: string) => {
				ctx.ui.notify(`✗ Memoria #${id} no encontrada`, "warning");
				return result(`Observation #${id} not found.`, { [verb]: false });
			};

			switch (action) {
				case "state": {
					if (!params.state) return result("action=state needs state.", { ok: false });
					const guard = needsId();
					if (guard) return guard;
					const obs = await apiOrMissing<{ id: number; title: string; state: string }>(
						`/api/observations/${id}/state?${namespaced()}`,
						{ method: "POST", body: JSON.stringify({ state: params.state }) },
					);
					if (!obs) {
						ctx.ui.notify(`✗ Memoria #${id} no encontrada o estado inválido`, "warning");
						return result(`Observation #${id} not found.`, { updated: false });
					}
					ctx.ui.notify(`✓ Memoria #${obs.id} → ${obs.state}`, "info");
					return result(`Observation #${obs.id} state: ${obs.state}`, boundedObservation(obs));
				}

				case "pin": {
					const guard = needsId();
					if (guard) return guard;
					const obs = await apiOrMissing<{ id: number; title: string; pinned: boolean }>(
						`/api/observations/${id}/pin?${namespaced()}`,
						{ method: "POST", body: JSON.stringify({ pinned: params.pinned ?? true }) },
					);
					if (!obs) return missingRow("updated");
					ctx.ui.notify(`✓ Memoria #${obs.id} pinned=${obs.pinned}`, "info");
					return result(`Observation #${obs.id} pinned: ${obs.pinned}`, boundedObservation(obs));
				}

				case "promote": {
					const guard = needsId();
					if (guard) return guard;
					const res = await fetch(
						`${baseUrl()}/api/observations/${id}/promote?${namespaced(params.allowArchived ? { allowArchived: "true" } : {})}`,
						{ method: "POST" },
					);
					if (res.status === 404) return missingRow("promoted");
					const body = (await res.json().catch(() => ({}))) as {
						observation?: { id: number; title: string };
						merged?: boolean;
						outcome?: string;
						reason?: string;
					};
					if (res.status === 409) {
						// A refusal is a result the model can act on (retry with allowArchived),
						// not a thrown error.
						ctx.ui.notify(`✗ Memoria #${id} archivada: promoción rechazada`, "warning");
						return result(
							`Promotion refused (${body.reason ?? "refused"}): observation #${id} is archived. Pass allowArchived=true to promote it anyway; the promoted row stays archived.`,
							body,
						);
					}
					if (!res.ok) throw new Error(`HanniGram ${res.status}: ${JSON.stringify(body) || res.statusText}`);
					const promoted = body.observation!;
					ctx.ui.notify(`✓ Memoria promovida #${promoted.id} → global (${body.outcome})`, "info");
					return result(
						`Promoted #${promoted.id} to global (${body.outcome}): ${bound(promoted.title, MAX_TITLE_CHARS)}`,
						body,
					);
				}

				case "update": {
					const guard = needsId();
					if (guard) return guard;
					const fields = pickFields(params.fields);
					if (Object.keys(fields).length === 0) return result("action=update needs fields.", { updated: false });
					const obs = await apiOrMissing<{ id: number; title: string; state?: string }>(
						`/api/observations/${id}?${namespaced()}`,
						{ method: "PUT", body: JSON.stringify(fields) },
					);
					if (!obs) return missingRow("updated");
					ctx.ui.notify(`✎ Memoria actualizada #${obs.id}: ${obs.title}`, "info");
					return result(`Updated observation #${obs.id}: ${bound(obs.title, MAX_TITLE_CHARS)}`, boundedObservation(obs));
				}

				case "delete": {
					const guard = needsId();
					if (guard) return guard;
					const res = await fetch(`${baseUrl()}/api/observations/${id}?${namespaced()}`, { method: "DELETE" });
					if (res.status === 404) return missingRow("deleted");
					if (!res.ok) {
						const body = await res.text().catch(() => "");
						throw new Error(`HanniGram ${res.status}: ${body || res.statusText}`);
					}
					ctx.ui.notify(`✓ Memoria eliminada #${id}`, "info");
					return result(`Deleted observation #${id}.`, { deleted: true, id });
				}

				case "review": {
					const items = await api<
						Array<{ kind: string; id: number; scope?: string; title: string; preview: string; duplicateOfId?: number }>
					>(
						`/api/review?${scope(ctx.cwd, {
							limit: String(params.limit ?? 20),
							...(rowScope ? { scope: rowScope } : {}),
						})}`,
					);
					if (items.length === 0) return result("Nothing to review.", []);
					const text = items
						.map(
							(i) =>
								`[${i.kind}] #${i.id} (${i.scope ?? "project"}) ${bound(i.title, MAX_TITLE_CHARS)}`
								+ (i.duplicateOfId ? ` ~ duplicate of #${i.duplicateOfId}` : "")
								+ `\n  ${i.preview}`,
						)
						.join("\n");
					return result(text, items);
				}

				case "session_summary": {
					if (!params.summary) return result("action=session_summary needs summary.", { ok: false });
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
					return result(`Session ${sessionId} summary saved.`, { sessionId });
				}

				case "suggest_topic_key": {
					const topics = await api<string[]>(`/api/topics?${scope(ctx.cwd, { limit: String(params.limit ?? 20) })}`);
					if (topics.length === 0) return result("No topic keys yet.", []);
					return result(topics.join("\n"), topics);
				}

				default:
					return result(`Unknown action '${action}'.`, { ok: false });
			}
		},
	});
}
