using Microsoft.EntityFrameworkCore;

namespace KasaSika.Application.Abstractions;

/// <summary>
/// A unique index rejected an insert. The DbContext translates the database-specific error into this so the
/// Application layer can react (idempotent create, duplicate callback, lost race for a submission) without
/// knowing which database is underneath. Subclasses DbUpdateException so existing handlers still catch it.
/// </summary>
public sealed class UniqueViolationException(string? constraint, Exception inner)
    : DbUpdateException($"Unique constraint violated: {constraint}", inner)
{
    public string? Constraint { get; } = constraint;
}
