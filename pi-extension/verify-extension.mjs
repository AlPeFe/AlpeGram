// Verification harness: loads the HanniGram Pi extension with jiti (the same
// loader Pi uses), captures the registered tools via a mock ExtensionAPI, and
// executes each mem_* tool against the running daemon.
import { createJiti } from "jiti";
import { fileURLToPath } from "node:url";

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
const EXPECTED_TOOLS = [
	"mem_context",
	"mem_current_project",
	"mem_delete",
	"mem_get_observation",
	"mem_pin",
	"mem_promote",
	"mem_review",
	"mem_save",
	"mem_search",
	"mem_session_summary",
	"mem_state",
	"mem_suggest_topic_key",
	"mem_timeline",
	"mem_update",
];
const registered = [...tools.keys()].sort();
const missing = EXPECTED_TOOLS.filter((n) => !registered.includes(n));
const extra = registered.filter((n) => !EXPECTED_TOOLS.includes(n));
if (missing.length > 0 || extra.length > 0) {
	throw new Error(`tool list mismatch: missing [${missing.join(", ")}], unexpected [${extra.join(", ")}]`);
}

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

// 1. current project
await run("mem_current_project", {});
// 2. save
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
// 3. search
await run("mem_search", { query: "verification" });
// 4. context
await run("mem_context", { limit: 5 });
// 5. topics
await run("mem_suggest_topic_key", {});
// 6. session summary
await run("mem_session_summary", {
	summary: "Verified HanniGram Pi extension",
	goal: "Confirm native tooling",
	nextSteps: "Commit and push",
	sessionId: "verify-sess",
});

// ---------------------------------------------------------------------------
// Slice 5 — the scope/lifecycle surface
// ---------------------------------------------------------------------------

// 7. global scope: a heuristic written once, shared by every project
const globalSaved = await run("mem_save", {
	title: "Global heuristic check",
	content: "kestrelheuristic marker for the shared cross-project scope",
	topicKey: "verification/global-heuristic",
	type: "pattern",
	scope: "global",
});
assert(
	globalSaved.content?.[0]?.text?.includes("(global)"),
	"mem_save scope=global did not report the global scope",
);

// 8. cross-scope search from ANOTHER project: the global hit must be visible and
//    labelled with its origin
const crossHit = await run("mem_search", { query: "kestrelheuristic", scope: "all" }, otherProject);
assert(
	crossHit.details?.some((h) => h.scope === "global"),
	"scope=all search from another project did not return the global hit",
);
assert(
	crossHit.content?.[0]?.text?.includes("(global)"),
	"the cross-project hit was not labelled with its origin",
);

// 9. update: mem_update edits the row in place
const updated = await run("mem_update", {
	id: savedId,
	learned: "juniperstate updated by mem_update",
});
assert(
	updated.content?.[0]?.text?.startsWith("Updated observation"),
	"mem_update did not report an update",
);

// 10. a project observation to promote
const promotable = await run("mem_save", {
	title: "Promotable project memory",
	content: "quartzpromote marker, a lesson that turned out to be generic",
	topicKey: "verification/promotable",
	type: "pattern",
});

// 11. lifecycle: flag it for review
const flagged = await run("mem_state", { id: promotable.details?.id, state: "needs_review" });
assert(
	flagged.content?.[0]?.text?.includes("needs_review"),
	"mem_state did not set needs_review",
);

// 12. the review listing is bounded and includes the flagged row
const review = await run("mem_review", { scope: "all" });
assert(
	review.details?.some((i) => i.id === promotable.details?.id),
	"mem_review did not list the row flagged needs_review",
);

// 13. promote: the project memory moves into the shared scope
const promoted = await run("mem_promote", { id: promotable.details?.id });
assert(
	promoted.content?.[0]?.text?.includes("moved"),
	"mem_promote did not report the move",
);

// 14. pin: only pinned global heuristics enter the automatic context budget
const pinned = await run("mem_pin", { id: globalSaved.details?.id, pinned: true, scope: "global" });
assert(
	pinned.content?.[0]?.text?.includes("pinned: true"),
	"mem_pin did not pin the global heuristic",
);

console.log(`\nTool list matches the expected ${EXPECTED_TOOLS.length} tools.`);

// ---------------------------------------------------------------------------
// Independent-verifier regressions: F2 (slash topic keys), F3 (bounded reads)
// and F4 (deleting a global memory). Each ran and failed before its fix.
// ---------------------------------------------------------------------------

// F2 — a slash topic key is exactly the documented shape ("architecture/auth-model")
// and MUST be retrievable through the extension's own call form.
const slashSaved = await run("mem_save", {
	title: "Slash key regression",
	content: "larkspurretrieval marker under a slash topic key",
	topicKey: "svc/auth",
	type: "pattern",
});
assert(slashSaved.details?.id !== undefined, "mem_save with a slash topicKey did not return an id");
const slashTimeline = await run("mem_timeline", { topicKey: "svc/auth" });
assert(
	Array.isArray(slashTimeline.details) && slashTimeline.details.length > 0,
	"mem_timeline returned nothing for the slash topic key 'svc/auth' (silent false negative)",
);
assert(
	slashTimeline.details.some((o) => o.id === slashSaved.details?.id),
	"mem_timeline did not return the row saved under the slash topic key",
);

// F2 — a plain key still works (the fix must not trade one broken form for another).
const plainSaved = await run("mem_save", {
	title: "Plain key regression",
	content: "larkspurplain marker under a plain topic key",
	topicKey: "svcplain",
	type: "pattern",
});
const plainTimeline = await run("mem_timeline", { topicKey: "svcplain" });
assert(
	Array.isArray(plainTimeline.details) && plainTimeline.details.some((o) => o.id === plainSaved.details?.id),
	"mem_timeline broke for a plain topic key",
);

// F3 — a 6000-character body must not reach the model intact: every field is bound
// (matching the MCP caps) and truncation is marked with an ellipsis.
const longBody = `zephyrlongmarker ${"x".repeat(6000)}`;
const longSaved = await run("mem_save", {
	title: "Long body regression",
	content: longBody,
	topicKey: "verification/long-body",
});
const longFetched = await run("mem_get_observation", { id: longSaved.details?.id });
const fetchedText = longFetched.content?.[0]?.text ?? "";
assert(
	fetchedText.length <= 2500,
	`mem_get_observation returned ${fetchedText.length} characters; the model-facing body must be bounded`,
);
assert(fetchedText.includes("\u2026"), "mem_get_observation did not mark the truncated body with an ellipsis");
assert(
	(longFetched.details?.content?.length ?? 0) <= 2001,
	"the bounded details payload still carried an unbounded body",
);
const longSearched = await run("mem_search", { query: "zephyrlongmarker" });
assert(
	(longSearched.details?.[0]?.content?.length ?? 0) <= 2001,
	"mem_search handed the model an unbounded body",
);

// F4 — mem_delete must reach a global memory with scope='global'.
const globalToDelete = await run("mem_save", {
	title: "Global delete regression",
	content: "larchdeletable marker in the shared global scope",
	topicKey: "verification/global-delete",
	scope: "global",
});
const deleteResult = await run("mem_delete", { id: globalToDelete.details?.id, scope: "global" });
assert(
	deleteResult.details?.deleted === true,
	"mem_delete with scope='global' did not delete the global memory",
);
const stillThere = await run("mem_search", { query: "larchdeletable", scope: "global" }, otherProject);
assert(
	(stillThere.details?.length ?? 0) === 0,
	"the global memory was reported deleted but is still searchable",
);

console.log("\nALL TOOLS EXECUTED OK");
