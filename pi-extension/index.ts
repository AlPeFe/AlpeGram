/**
 * HanniGram — native Pi extension.
 *
 * Registers the mem_* tools that talk to the HanniGram daemon over HTTP
 * (default http://127.0.0.1:8765). No MCP: the harness consumes HanniGram
 * natively as tooling, exactly like any built-in Pi tool.
 *
 * The daemon resolves the project from the session cwd (git remote when
 * available), so memory is scoped per project automatically.
 */

import type { ExtensionAPI, ExtensionToolContext } from "@earendil-works/pi-coding-agent";
import { Type } from "typebox";

const DEFAULT_BASE = "http://127.0.0.1:8765";

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

/** Build the query string carrying the project scope from the session cwd. */
function scope(cwd: string, extra: Record<string, string> = {}): string {
	const p = new URLSearchParams({ cwd, ...extra });
	return p.toString();
}

export default function (pi: ExtensionAPI) {
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
			"Save a durable, structured memory observation for the CURRENT project (scoped by git remote). Two kinds of memory, both per-project: (1) decisions & conventions the agent must RESPECT — set type='adr' so they are always surfaced to the agent; (2) context/ideas in progress — discoveries, patterns, bugfixes, config, concepts being worked on (any other type). Capture durable knowledge, never raw tool output or every turn. Give a short searchable title.",
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
		}),
		execute: async (_id, params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			const res = await api<{ observation: { id: number; title: string; topicKey?: string }; created: boolean }>(
				`/api/observations?${scope(ctx.cwd)}`,
				{
					method: "POST",
					body: JSON.stringify(params),
				},
			);
			const { observation: obs, created } = res;
			ctx.ui.notify(
				created
					? `✓ Memoria guardada #${obs.id}: ${obs.title}${obs.topicKey ? ` [${obs.topicKey}]` : ""}`
					: `✎ Memoria actualizada #${obs.id}: ${obs.title}${obs.topicKey ? ` [${obs.topicKey}]` : ""}`,
				"info",
			);
			return {
				content: [
					{
						type: "text",
						text: `${created ? "Saved" : "Updated"} observation #${obs.id}: ${obs.title}${obs.topicKey ? ` [topic: ${obs.topicKey}]` : ""}`,
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
			"Full-text search over the current project's memory (SQLite FTS5). Use before revisiting a decision, bug, convention, or request that may already be known. Results are previews, not the complete record.",
		parameters: Type.Object({
			query: Type.String({ description: "Search terms; all terms must match" }),
			limit: Type.Optional(Type.Number({ description: "Max results (default 10)" })),
			type: Type.Optional(Type.String({ description: "Filter by observation type (e.g. 'adr', 'decision', 'bugfix', 'pattern')" })),
			topicKey: Type.Optional(Type.String({ description: "Restrict to a single topic key, e.g. 'ui/palette'" })),
		}),
		execute: async (_id, params, _signal, _onUpdate, ctx: ExtensionToolContext) => {
			const hits = await api<Array<{ id: number; title: string; content: string; topicKey?: string; type?: string }>>(
				`/api/search?${scope(ctx.cwd, {
					q: params.query,
					limit: String(params.limit ?? 10),
					...(params.type ? { type: params.type } : {}),
					...(params.topicKey ? { topicKey: params.topicKey } : {}),
				})}`,
			);
			if (hits.length === 0) {
				return { content: [{ type: "text", text: "No memory matches." }], details: [] };
			}
			const text = hits
				.map((h) => `#${h.id} ${h.title}${h.topicKey ? ` [${h.topicKey}]` : ""}\n  ${h.content}`)
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
}
