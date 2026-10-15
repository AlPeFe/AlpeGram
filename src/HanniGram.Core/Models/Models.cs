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
