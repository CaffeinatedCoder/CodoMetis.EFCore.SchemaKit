using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.SqlServer;

// A contributor for SQL Server as a package outside the repository has to write one: against the
// kernel's public contract, with no InternalsVisibleTo. It is not the beginning of the SQL Server
// package (#29) and ships nowhere. What it could not say, or could say only by going around
// something, is the evidence #119 asks for; each such place is marked "Audit:".

/// <summary>
/// A nonclustered index of SQL Server: key columns, included columns, a filter. Columns only: SQL
/// Server indexes no expression.
/// </summary>
/// <param name="Keys">The key columns, as property paths, in their order.</param>
public sealed record ProbeIndex(ValueList<string> Keys) : ISchemaDeclaration<ProbeIndex>
{
    public static string OwnerId => "Probe.SqlServerIndex";

    public string Owner => OwnerId;

    public string Kind => "nonclustered";

    /// <summary>The index's name, or null for <c>IX_{table}_{keys}</c>, which is how EF Core names its own.</summary>
    public string? Name { get; init; }

    public int Epoch { get; init; } = 1;

    /// <summary>The columns carried in the leaf level and not in the key.</summary>
    public ValueList<string> Include { get; init; } = [];

    /// <summary>The rows the index covers, or null for all of them.</summary>
    /// <remarks>
    /// Audit: a template written by hand. <see cref="Predicate.Of{TEntity}" /> wants a
    /// <see cref="SqlSpelling" />, and the kernel has one, PostgreSQL's, behind a private
    /// constructor: a second provider has nothing to pass, and SQL Server refuses what the one
    /// there is writes (<c>PublicContractTests</c>, <c>SqlServerSpellingLiveTests</c>).
    /// </remarks>
    public Predicate? Where { get; init; }

    public bool IsUnique { get; init; }

    /// <summary>
    /// Audit: the nearest the contract has. SQL Server compares names by the database's collation,
    /// which is neither of the two cases and is not a property of a declaration
    /// (<c>SqlServerNameLiveTests</c>).
    /// </summary>
    public SchemaNameComparison NameComparison => SchemaNameComparison.IgnoreCase;

    public void Write(SchemaJsonWriter writer)
    {
        writer.Strings("keys", Keys);
        writer.Strings("include", Include);
        writer.OptionalString("where", Where?.Template);
        writer.Boolean("unique", IsUnique);
    }

    public static ProbeIndex Read(SchemaDeclarationHeader header, SchemaJsonReader reader)
        => new(reader.Strings("keys"))
        {
            Name = header.Name,
            Epoch = header.Epoch,
            Include = reader.Strings("include"),
            Where = reader.OptionalString("where") is { } where ? new Predicate(where) : null,
            IsUnique = reader.Boolean("unique"),
        };
}

/// <summary>The entry point a consumer would have: <c>DeclareSqlServer</c>, in the real package.</summary>
public static class ProbeIndexDeclarationExtensions
{
    public static EntityTypeBuilder<TEntity> DeclareProbe<TEntity>(this EntityTypeBuilder<TEntity> builder, params ProbeIndex[] indexes)
        where TEntity : class
    {
        builder.Metadata.Declare(indexes);
        return builder;
    }
}
