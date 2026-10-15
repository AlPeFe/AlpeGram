namespace HanniGram.Core.Models;

/// <summary>
/// A project that owns a memory namespace. Resolved from a working directory
/// (cwd) or an explicit name, mirroring engram's project-aware reads.
/// </summary>
public sealed class Project
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string? RootPath { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// A durable, structured memory observation. The core unit of HanniGram memory.
/// Mirrors engram's What/Why/Where/Learned shape.
/// </summary>
public sealed class Observation
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public string? What { get; set; }
    public string? Why { get; set; }
    public string? Where { get; set; }
    public string? Learned { get; set; }
    public string? TopicKey { get; set; }
    public string? Type { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    /// <summary>
    /// Lifecycle state: "active", "needs_review" or "archived". Default reads
    /// only return "active"; archived is the safe forget.
    /// </summary>
    public string State { get; set; } = MemoryStates.Active;
    /// <summary>
    /// Pinned flag. Pinned global heuristics are the only global memory allowed
    /// into the automatic context path (the bounded injection budget).
    /// </summary>
    public bool Pinned { get; set; }
    /// <summary>
    /// Origin scope ("project" or "global"). Lets a caller tell a repo fact
    /// from a universal heuristic. Set on every read path.
    /// </summary>
    public string? Scope { get; set; }
}

/// <summary>
/// A coding-agent session bound to a project. Sessions carry a handoff summary
/// (session_summary) so the next session can recover context.
/// </summary>
public sealed class Session
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public required string SessionId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public string? Summary { get; set; }
    public string? Goal { get; set; }
    public string? NextSteps { get; set; }
}

/// <summary>Search hit returned by FTS5.</summary>
public sealed class SearchHit
{
    public long Id { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }
    public string? TopicKey { get; set; }
    public string? Type { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public double Rank { get; set; }
    /// <summary>
    /// Lifecycle state of the hit. Only relevant when the search is asked for a
    /// non-default state (a default search returns active rows only).
    /// </summary>
    public string? State { get; set; }
    /// <summary>
    /// Origin scope ("project" or "global") of the hit, so a cross-scope search
    /// can distinguish a repo fact from a universal heuristic.
    /// </summary>
    public string? Scope { get; set; }
}

/// <summary>
/// One namespace to include in a cross-scope read: a project id plus the origin
/// label ("project" or "global") stamped onto every row it returns.
/// </summary>
public readonly record struct MemoryNamespace(long ProjectId, string Scope);

/// <summary>
/// The compact review hint a save returns when it detected a collision. A few
/// tokens only: the colliding id, its truncated title and the kind of collision.
/// It is absent (null) when there is nothing to report. This is the entire
/// review mechanism on the hot path — it never carries memory bodies.
/// </summary>
public sealed class SaveReviewHint
{
    /// <summary>"topic_key" (an upsert overwrote an existing keyed row) or "duplicate".</summary>
    public required string Kind { get; set; }
    /// <summary>The id of the row the save collided with.</summary>
    public long Id { get; set; }
    /// <summary>The colliding row's title, truncated to a few dozen characters.</summary>
    public required string Title { get; set; }
}

/// <summary>
/// One bounded entry of the review listing. Previews are truncated; the review
/// response never returns an observation body.
/// </summary>
public sealed class ReviewItem
{
    /// <summary>"needs_review" or "duplicate".</summary>
    public required string Kind { get; set; }
    public long Id { get; set; }
    public required string Title { get; set; }
    public required string Preview { get; set; }
    public string? State { get; set; }
    public string? Scope { get; set; }
    /// <summary>For a duplicate candidate, the id of the earlier row it duplicates.</summary>
    public long? DuplicateOfId { get; set; }
}
