// Verification harness: loads the HanniGram Pi extension with jiti (the same
// loader Pi uses), captures the registered tools via a mock ExtensionAPI, and
// executes each mem_* tool against the running daemon.
//
// Since SPEC-002 the surface is four tools, so this file checks two things:
//   1. every legacy capability still works through its new route (the legacy
//      map below is the acceptance list, and the README must document it);
//   2. the independent-verifier regressions (F2 slash topic keys, F3 bounded
//      reads, F4 deleting a global memory) still hold.
import { createJiti } from "jiti";
import { readFileSync } from "node:fs";

const jiti = createJiti(import.meta.url, { moduleCache: false });
const factory = await jiti.import(new URL("./index.ts", import.meta.url).href, { default: true });

const tools = new Map();
const mockPi = {
	registerTool: (t) => tools.set(t.name, t),
	registerCommand: () => {},
	on: () => () => {},
};

await factory(mockPi);
console.log(`Loaded extension. Registered ${tools.size} tools: ${[...tools.keys()].join(", ")}`);

// The tool list is the model-facing surface: it is asserted, not just printed, so
// a silently dropped or renamed tool fails the verification.
const EXPECTED_TOOLS = ["mem_admin", "mem_get", "mem_save", "mem_search"];
const registered = [...tools.keys()].sort();
const missing = EXPECTED_TOOLS.filter((n) => !registered.includes(n));
const extra = registered.filter((n) => !EXPECTED_TOOLS.includes(n));
if (missing.length > 0 || extra.length > 0) {
	throw new Error(`tool list mismatch: missing [${missing.join(", ")}], unexpected [${extra.join(", ")}]`);
}

/**
 * SPEC-002: every pre-consolidation tool must keep a documented route to its
 * replacement. [legacy name, new tool, selector within that tool] — the README
 * table is asserted against this, so the mapping cannot rot silently.
 */
const LEGACY_ROUTES = [
	["mem_search", "mem_search", ""],
	["mem_save", "mem_save", ""],
	["mem_current_project", "mem_get", "kind=project"],
	["mem_context", "mem_get", "kind=context"],
	["mem_get_observation", "mem_get", "kind=id"],
	["mem_timeline", "mem_get", "kind=topic"],
	["mem_state", "mem_admin", "action=state"],
	["mem_pin", "mem_admin", "action=pin"],
	["mem_promote", "mem_admin", "action=promote"],
	["mem_update", "mem_admin", "action=update"],
	["mem_delete", "mem_admin", "action=delete"],
	["mem_review", "mem_admin", "action=review"],
	["mem_session_summary", "mem_admin", "action=session_summary"],
	["mem_suggest_topic_key", "mem_admin", "action=suggest_topic_key"],
];

const readme = readFileSync(new URL("../README.md", import.meta.url), "utf8").split("\n");
for (const [legacy, tool, selector] of LEGACY_ROUTES) {
	const documented = readme.some(
		(line) => line.includes(`\`${legacy}\``) && line.includes(`\`${tool}\``) && line.includes(selector),
	);
	if (!documented) {
		throw new Error(`README has no route for the removed tool '${legacy}': expected a table row naming \`${tool}\` ${selector}`);
	}
}
console.log(`README documents a route for all ${LEGACY_ROUTES.length} pre-SPEC-002 tools.`);

function assert(ok, message) {
	if (!ok) throw new Error(`assertion failed: ${message}`);
}

// Minimal fake ctx with a cwd and a ui.notify stub (tools call it on write).
const ctx = { cwd: "C:/Users/alexlocal/projects/AlpefePI", ui: { notify: () => {} } };
// A second project, to prove a global memory saved from one cwd is retrievable
// from another one.
const otherProject = { cwd: "C:/Users/alexlocal/projects/KwyjiboHarness", ui: { notify: () => {} } };

async function run(name, params, context = ctx) {
	const t = tools.get(name);
	if (!t) throw new Error(`tool ${name} not registered`);
	const res = await t.execute("test-id", params, undefined, undefined, context);
	const text = res.content?.map((c) => c.text).join("\n") ?? "";
	console.log(`\n### ${name} ${JSON.stringify(params)}\n${text}`);
	return res;
}

const textOf = (res) => res.content?.[0]?.text ?? "";

// 1. was mem_current_project
const project = await run("mem_get", { kind: "project" });
assert(textOf(project).startsWith("Project: "), "mem_get kind=project did not report the resolved project");

// 2. save: every structured field survives the round trip
const saved = await run("mem_save", {
	title: "Verification observation",
	content: "HanniGram Pi extension verified end-to-end via jiti harness",
	what: "Ran verification",
	why: "Confirm native integration",
	where: "pi-extension/index.ts",
	learned: "jiti loads the extension and tools execute against the daemon",
	topicKey: "verification/integration",
	type: "discovery",
});
const savedId = saved.details?.id;
assert(savedId !== undefined, "mem_save did not return an observation id");
assert(textOf(saved).startsWith("Saved observation"), "mem_save did not report a created row");

// 3. was mem_search
const searched = await run("mem_search", { query: "verification" });
assert(searched.details?.some((h) => h.id === savedId), "mem_search did not find the row just saved");

// 4. was mem_context
const recent = await run("mem_get", { kind: "context", limit: 5 });
assert(Array.isArray(recent.details) && recent.details.length > 0, "mem_get kind=context returned no recent rows");

// 5. was mem_get_observation, with every field intact
const fetched = await run("mem_get", { kind: "id", id: savedId });
assert(fetched.details?.where === "pi-extension/index.ts", "mem_get kind=id lost the 'where' field");
assert(textOf(fetched).includes("learned: "), "mem_get kind=id did not render the 'learned' field");

// 6. was mem_suggest_topic_key
const topics = await run("mem_admin", { action: "suggest_topic_key" });
assert(topics.details?.includes("verification/integration"), "mem_admin action=suggest_topic_key missed the saved topic key");

// 7. was mem_session_summary
const summary = await run("mem_admin", {
	action: "session_summary",
	summary: "Verified HanniGram Pi extension",
	goal: "Confirm native tooling",
	nextSteps: "Commit and push",
	sessionId: "verify-sess",
});
assert(summary.details?.sessionId === "verify-sess", "mem_admin action=session_summary did not report the session id");

// 8. a guard, not a crash: a row action without an id is a result
const noId = await run("mem_admin", { action: "state", state: "active" });
assert(textOf(noId) === "action=state needs an id.", "mem_admin did not answer a missing id with a usable message");

// ---------------------------------------------------------------------------
// Slice 5 — the scope/lifecycle surface
// ---------------------------------------------------------------------------

// 9. global scope: a heuristic written once, shared by every project
const globalSaved = await run("mem_save", {
	title: "Global heuristic check",
	content: "kestrelheuristic marker for the shared cross-project scope",
	topicKey: "verification/global-heuristic",
	type: "pattern",
	scope: "global",
});
assert(textOf(globalSaved).includes("(global)"), "mem_save scope=global did not report the global scope");

// 10. cross-scope search from ANOTHER project: the global hit must be visible and
//    labelled with its origin
const crossHit = await run("mem_search", { query: "kestrelheuristic", scope: "all" }, otherProject);
assert(
	crossHit.details?.some((h) => h.scope === "global"),
	"scope=all search from another project did not return the global hit",
);
assert(textOf(crossHit).includes("(global)"), "the cross-project hit was not labelled with its origin");

// 11. was mem_update: only the patched field changes
const updated = await run("mem_admin", {
	action: "update",
	id: savedId,
	fields: { learned: "juniperstate updated by mem_admin action=update" },
});
assert(textOf(updated).startsWith("Updated observation"), "mem_admin action=update did not report an update");
const afterUpdate = await run("mem_get", { kind: "id", id: savedId });
assert(
	afterUpdate.details?.learned === "juniperstate updated by mem_admin action=update",
	"mem_admin action=update did not persist the patched field",
);
assert(afterUpdate.details?.title === "Verification observation", "mem_admin action=update changed an untouched field");

// 12. a project observation to promote
const promotable = await run("mem_save", {
	title: "Promotable project memory",
	content: "quartzpromote marker, a lesson that turned out to be generic",
	topicKey: "verification/promotable",
	type: "pattern",
});

// 13. was mem_state: flag it for review
const flagged = await run("mem_admin", { action: "state", id: promotable.details?.id, state: "needs_review" });
assert(textOf(flagged).includes("needs_review"), "mem_admin action=state did not set needs_review");

// 14. was mem_review: the listing is bounded and includes the flagged row
const review = await run("mem_admin", { action: "review", scope: "all" });
assert(
	review.details?.some((i) => i.id === promotable.details?.id),
	"mem_admin action=review did not list the row flagged needs_review",
);

// 15. was mem_promote: the project memory moves into the shared scope
const promoted = await run("mem_admin", { action: "promote", id: promotable.details?.id });
assert(textOf(promoted).includes("moved"), "mem_admin action=promote did not report the move");

// 16. was mem_pin: only pinned global heuristics enter the automatic context budget
const pinned = await run("mem_admin", { action: "pin", id: globalSaved.details?.id, pinned: true, scope: "global" });
assert(textOf(pinned).includes("pinned: true"), "mem_admin action=pin did not pin the global heuristic");

console.log(`\nTool list matches the expected ${EXPECTED_TOOLS.length} tools.`);

// ---------------------------------------------------------------------------
// Independent-verifier regressions: F2 (slash topic keys), F3 (bounded reads)
// and F4 (deleting a global memory). Each ran and failed before its fix.
// ---------------------------------------------------------------------------

// F2 — a slash topic key is exactly the documented shape ("architecture/auth-model")
// and MUST be retrievable through the extension's own call form (now mem_get kind=topic).
const slashSaved = await run("mem_save", {
	title: "Slash key regression",
	content: "larkspurretrieval marker under a slash topic key",
	topicKey: "svc/auth",
	type: "pattern",
});
assert(slashSaved.details?.id !== undefined, "mem_save with a slash topicKey did not return an id");
const slashTopic = await run("mem_get", { kind: "topic", topicKey: "svc/auth" });
assert(
	Array.isArray(slashTopic.details) && slashTopic.details.length > 0,
	"mem_get kind=topic returned nothing for the slash topic key 'svc/auth' (silent false negative)",
);
assert(
	slashTopic.details.some((o) => o.id === slashSaved.details?.id),
	"mem_get kind=topic did not return the row saved under the slash topic key",
);

// F2 — a plain key still works (the fix must not trade one broken form for another).
const plainSaved = await run("mem_save", {
	title: "Plain key regression",
	content: "larkspurplain marker under a plain topic key",
	topicKey: "svcplain",
	type: "pattern",
});
const plainTopic = await run("mem_get", { kind: "topic", topicKey: "svcplain" });
assert(
	Array.isArray(plainTopic.details) && plainTopic.details.some((o) => o.id === plainSaved.details?.id),
	"mem_get kind=topic broke for a plain topic key",
);

// F3 — a 6000-character body must not reach the model intact: every field is bound
// (matching the MCP caps) and truncation is marked with an ellipsis.
const longBody = `zephyrlongmarker ${"x".repeat(6000)}`;
const longSaved = await run("mem_save", {
	title: "Long body regression",
	content: longBody,
	topicKey: "verification/long-body",
});
const longFetched = await run("mem_get", { kind: "id", id: longSaved.details?.id });
const fetchedText = textOf(longFetched);
assert(
	fetchedText.length <= 2500,
	`mem_get kind=id returned ${fetchedText.length} characters; the model-facing body must be bounded`,
);
assert(fetchedText.includes("\u2026"), "mem_get kind=id did not mark the truncated body with an ellipsis");
assert(
	(longFetched.details?.content?.length ?? 0) <= 2001,
	"the bounded details payload still carried an unbounded body",
);
const longSearched = await run("mem_search", { query: "zephyrlongmarker" });
assert(
	(longSearched.details?.[0]?.content?.length ?? 0) <= 2001,
	"mem_search handed the model an unbounded body",
);

// F4 — action=delete must reach a global memory with scope='global'.
const globalToDelete = await run("mem_save", {
	title: "Global delete regression",
	content: "larchdeletable marker in the shared global scope",
	topicKey: "verification/global-delete",
	scope: "global",
});
const deleteResult = await run("mem_admin", { action: "delete", id: globalToDelete.details?.id, scope: "global" });
assert(deleteResult.details?.deleted === true, "mem_admin action=delete with scope='global' did not delete the global memory");
const stillThere = await run("mem_search", { query: "larchdeletable", scope: "global" }, otherProject);
assert((stillThere.details?.length ?? 0) === 0, "the global memory was reported deleted but is still searchable");

console.log("\nALL TOOLS EXECUTED OK");
