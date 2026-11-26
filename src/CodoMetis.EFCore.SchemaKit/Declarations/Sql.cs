using CodoMetis.EFCore.SchemaKit.Infrastructure.Internal;

namespace CodoMetis.EFCore.SchemaKit;

/// <summary>SQL written by hand, inside the lambda of a declaration.</summary>
public static class Sql
{
    /// <summary>
    /// Stands for SQL written by hand in the lambda of a key, an element or a condition:
    /// <c>x => Sql.Raw&lt;string&gt;("lower({Email})")</c>,
    /// <c>where: x => Sql.Raw&lt;bool&gt;("{DeletedOn} IS NULL")</c>. It is read from the lambda
    /// and never called.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>{Path}</c> outside a single-quoted literal stands for the column of that property path,
    /// and is the only way a column is known to be read. Everything else reaches the database as
    /// it is written: nothing checks it, and nothing delimits a name in it. The object it is part
    /// of is taken to read every column of its table.
    /// </para>
    /// <para>
    /// As the whole lambda it is the SQL as written. As a part of a larger expression it stands
    /// in parentheses. No null handling is derived from the text: a <typeparamref name="T" /> that
    /// is a value type says the SQL is never NULL, a <c>Nullable&lt;T&gt;</c> or a reference type
    /// that it can be.
    /// </para>
    /// </remarks>
    /// <param name="sql">The SQL. A value read from outside the lambda is read where the lambda is written.</param>
    /// <typeparam name="T">What the SQL evaluates to, for the lambda around it.</typeparam>
    /// <exception cref="InvalidOperationException">Always, when called: it is a marker.</exception>
    public static T Raw<T>(string sql) => throw new InvalidOperationException(SchemaKitStrings.RawIsAMarker);
}
