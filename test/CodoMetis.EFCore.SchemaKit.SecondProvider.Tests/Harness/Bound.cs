using System.Linq.Expressions;
using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Sqlite;
using CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CodoMetis.EFCore.SchemaKit.SecondProvider.Tests.Harness;

/// <summary>
/// What the one spelling writes for a lambda, read the only way the contract shows it: from a
/// model that declared it and was finalized.
/// </summary>
/// <remarks>
/// Audit: <see cref="Predicate.Of{TEntity}" /> and <see cref="KeyTarget.Of" /> return a value that
/// is not SQL until its model is finalized (issue 114), and nothing in the contract binds one
/// without a model. A package's test of its own spelling builds a model for it, as here.
/// </remarks>
internal static class Bound
{
    /// <summary>The condition as the template shows it: a column as <c>{Path}</c>.</summary>
    public static string Where<TEntity>(Expression<Func<TEntity, bool>> condition)
        where TEntity : class
    {
        using var context = new OneEntity<TEntity>(
            options => options.UseSqlServer(Contexts.OfflineSqlServer, o => o.UseSchemaKitProbe()),
            entity => entity.Metadata.Declare([new ProbeIndex(["Bound"]) { Name = "IX_bound", Where = Predicate.Of(condition, SqlSpelling.PostgreSql) }]));

        return context.Entity.GetDeclarations<ProbeIndex>().Single().Where!.Template.ToString();
    }

    /// <summary>The key as it was declared: a column, or an expression that is bound.</summary>
    public static KeyTarget Key<TEntity, TValue>(Expression<Func<TEntity, TValue>> key)
        where TEntity : class
    {
        using var context = new OneEntity<TEntity>(
            options => options.UseSqlite(Contexts.OfflineSqlite, o => o.UseSchemaKitProbe()),
            entity => entity.Metadata.Declare([new ProbeSqliteIndex(KeyTarget.Of(key, SqlSpelling.PostgreSql)) { Name = "IX_bound" }]));

        return context.Entity.GetDeclarations<ProbeSqliteIndex>().Single().Key;
    }

    private sealed class OneEntity<TEntity>(Action<DbContextOptionsBuilder> configure, Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity>> declare) : DbContext
        where TEntity : class
    {
        public IReadOnlyEntityType Entity => this.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(TEntity))!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            configure(optionsBuilder);
            optionsBuilder
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
                .ReplaceService<IModelCacheKeyFactory, PerContext>();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) => declare(modelBuilder.Entity<TEntity>().HasNoKey());

        // One model for each lambda: the model would otherwise be the first lambda's.
        private sealed class PerContext : IModelCacheKeyFactory
        {
            public object Create(DbContext context, bool designTime) => (context, designTime);
        }
    }
}
