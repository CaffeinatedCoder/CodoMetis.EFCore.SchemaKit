using CodoMetis.EFCore.SchemaKit.PostgreSQL.Internal;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL;

/// <summary>
/// What <c>DeclarePostgres</c> accepts: a declaration PostgreSQL renders. Another package's
/// PostgreSQL declarations implement it and go into the same list; a declaration for another
/// provider does not, and does not compile there.
/// </summary>
public interface IPostgresObject : ISchemaObject;

/// <summary>
/// A PostgreSQL exclusion constraint: no two rows may satisfy every element's operator at once.
/// One case per access method that can back one. A value: compare it, keep it in a field, change it
/// with <c>with</c>.
/// </summary>
/// <remarks>
/// <para>
/// GiST takes several elements, SP-GiST one. B-tree and hash can only back a constraint that is a
/// unique constraint in other words, and GIN and BRIN cannot back one at all, so they are no cases.
/// </para>
/// <para>
/// Stored as one annotation, <c>CodoMetis:Pg.Exclusion:{hash}</c>. The owner, the kinds and the
/// property names written below are in consumers' snapshots: a file format.
/// </para>
/// </remarks>
public closed record PgExclusion : IPostgresObject, ISchemaDeclaration<PgExclusion>
{
    /// <summary>The owner of every exclusion constraint: the middle part of its annotation key.</summary>
    public static string OwnerId => "Pg.Exclusion";

    /// <inheritdoc />
    public string Owner => OwnerId;

    /// <summary>The constraint's name, or null for <c>EX_{table}_{elements}</c>.</summary>
    public string? Name { get; init; }

    /// <inheritdoc />
    public int Epoch { get; init; } = 1;

    /// <summary>The rows the constraint covers, or null for all of them.</summary>
    public Predicate? Where { get; init; }

    /// <summary>When the constraint is checked: at the statement, or at commit.</summary>
    public Deferral Deferral
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(value));
    } = Deferral.NotDeferrable;

    /// <inheritdoc />
    public string Kind => this switch
    {
        Gist   => "gist",
        SpGist => "spgist",
    };

    /// <summary>The elements in their order, whichever case this is.</summary>
    internal ValueList<ExclusionElement> ElementList => this switch
    {
        Gist gist     => gist.Elements,
        SpGist spGist => [spGist.Element],
    };

    /// <summary>
    /// The same constraint over fewer rows: its condition and <paramref name="predicate" />. One
    /// that is already the condition, or its last conjunct, is not added again.
    /// </summary>
    /// <param name="predicate">The condition to add.</param>
    public PgExclusion AndWhere(Predicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return this with { Where = Where?.And(predicate) ?? predicate };
    }

    /// <inheritdoc />
    public void Write(SchemaJsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _ = this switch
        {
            Gist gist     => Elements(gist),
            SpGist spGist => Element(spGist),
        };
        writer.OptionalString("where", Where?.Template);
        writer.String("deferral", Deferral.Token);

        bool Elements(Gist gist)
        {
            writer.Array("elements", gist.Elements, WriteElement);
            return true;
        }

        bool Element(SpGist spGist)
        {
            writer.Object("element", element => WriteElement(element, spGist.Element));
            return true;
        }
    }

    /// <inheritdoc />
    public static PgExclusion Read(SchemaDeclarationHeader header, SchemaJsonReader reader)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(reader);

        PgExclusion constraint = header.Kind switch
        {
            "gist"   => new Gist(reader.Array("elements", ReadElement)),
            "spgist" => new SpGist(reader.Object("element", ReadElement)),
            var kind => throw new InvalidOperationException(PgStrings.UnknownKind(OwnerId, kind)),
        };

        return constraint with
        {
            Name = header.Name,
            Epoch = header.Epoch,
            Where = reader.OptionalString("where") is { } where ? new Predicate(where) : null,
            Deferral = Deferral.Parse(reader.String("deferral")),
        };
    }

    // An element's target is written under the name of its case, and exactly one of the two is
    // there. A reader that met neither, or both, would have to pick, and whichever it picked would
    // be a constraint nobody declared.
    private static void WriteElement(SchemaJsonWriter writer, ExclusionElement element)
    {
        writer.OptionalString("column", (element.Target as KeyTarget.Column)?.Path);
        writer.OptionalString("sql", (element.Target as KeyTarget.Template)?.Sql);
        writer.String("op", element.Operator.Token);
    }

    private static ExclusionElement ReadElement(SchemaJsonReader reader)
    {
        var column = reader.OptionalString("column");
        var sql = reader.OptionalString("sql");
        KeyTarget target = (column, sql) switch
        {
            ({ } path, null)     => new KeyTarget.Column(path),
            (null, { } template) => new KeyTarget.Template(template),
            _                    => throw new InvalidOperationException(PgStrings.ElementNeedsOneTarget),
        };

        return new ExclusionElement(target, new ExclusionOperator(reader.String("op")));
    }

    /// <summary>
    /// An equality-only constraint is a unique constraint under a costlier index, and an empty one
    /// is a syntax error. The factory's stage types rule both out; this is for what goes around
    /// them: <c>with</c>, a constructor call, a snapshot.
    /// </summary>
    private static ValueList<ExclusionElement> RequireCompared(ValueList<ExclusionElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        return elements.Any(element => element.Operator != ExclusionOperator.Equal)
            ? elements
            : throw new ArgumentException(PgStrings.ExclusionOfEqualityOnly, nameof(elements));
    }

    /// <summary>Backed by a GiST index: any number of elements.</summary>
    /// <param name="Elements">The elements, in the order they are written. At least one compares with something other than <c>=</c>.</param>
    public sealed record Gist(ValueList<ExclusionElement> Elements) : PgExclusion
    {
        /// <summary>The elements, in the order they are written.</summary>
        public ValueList<ExclusionElement> Elements
        {
            get;
            init => field = RequireCompared(value);
        } = RequireCompared(Elements);
    }

    /// <summary>Backed by an SP-GiST index, which has one column.</summary>
    /// <param name="Element">The element. It compares with something other than <c>=</c>.</param>
    public sealed record SpGist(ExclusionElement Element) : PgExclusion
    {
        /// <summary>The element.</summary>
        public ExclusionElement Element
        {
            get;
            init => field = RequireCompared([value ?? throw new ArgumentNullException(nameof(value))])[0];
        } = RequireCompared([Element ?? throw new ArgumentNullException(nameof(Element))])[0];
    }
}

/// <summary>One element of an exclusion constraint: what is compared, and the operator two rows must not both satisfy.</summary>
/// <param name="Target">A column or an expression.</param>
/// <param name="Operator">The operator.</param>
public sealed record ExclusionElement(KeyTarget Target, ExclusionOperator Operator)
{
    /// <summary>A column or an expression.</summary>
    public KeyTarget Target
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(value));
    } = Target ?? throw new ArgumentNullException(nameof(Target));

    /// <summary>The operator.</summary>
    public ExclusionOperator Operator
    {
        get;
        init => field = value ?? throw new ArgumentNullException(nameof(value));
    } = Operator ?? throw new ArgumentNullException(nameof(Operator));
}

/// <summary>
/// The operator of an exclusion element. The factory picks <see cref="Equal" /> and
/// <see cref="Overlaps" /> by the operand's type; any other is given by its characters, and whether
/// the operand's type has it is then the database's to say.
/// </summary>
/// <param name="Token">The operator's characters, such as <c>-|-</c>. The dialect checks them against PostgreSQL's operator grammar.</param>
public sealed record ExclusionOperator(string Token)
{
    /// <summary>The operator's characters.</summary>
    public string Token
    {
        get;
        init => field = Required(value);
    } = Required(Token);

    // A record's `with` goes through init and not through the constructor, so the rule is in both.
    private static string Required(string token)
        => !string.IsNullOrWhiteSpace(token) ? token : throw new ArgumentException(PgStrings.OperatorNeedsCharacters, nameof(Token));

    /// <summary><c>=</c></summary>
    public static ExclusionOperator Equal { get; } = new("=");

    /// <summary><c>&amp;&amp;</c>: two ranges, multiranges or arrays share an element.</summary>
    public static ExclusionOperator Overlaps { get; } = new("&&");
}

/// <summary>
/// When an exclusion constraint is checked. A closed set of three, not two flags: "initially
/// deferred but not deferrable" has no spelling.
/// </summary>
public closed record Deferral
{
    private sealed record NotDeferrableCase : Deferral;

    private sealed record InitiallyImmediateCase : Deferral;

    private sealed record InitiallyDeferredCase : Deferral;

    /// <summary>Checked at the end of each statement, always. PostgreSQL's default.</summary>
    public static Deferral NotDeferrable { get; } = new NotDeferrableCase();

    /// <summary><c>DEFERRABLE</c>: checked at the end of each statement, unless a transaction defers it.</summary>
    public static Deferral InitiallyImmediate { get; } = new InitiallyImmediateCase();

    /// <summary><c>DEFERRABLE INITIALLY DEFERRED</c>: checked at commit.</summary>
    public static Deferral InitiallyDeferred { get; } = new InitiallyDeferredCase();

    /// <summary>How the case is stored. In consumers' snapshots: a file format.</summary>
    internal string Token => this switch
    {
        NotDeferrableCase      => "notDeferrable",
        InitiallyImmediateCase => "initiallyImmediate",
        InitiallyDeferredCase  => "initiallyDeferred",
    };

    internal static Deferral Parse(string token) => token switch
    {
        "notDeferrable"      => NotDeferrable,
        "initiallyImmediate" => InitiallyImmediate,
        "initiallyDeferred"  => InitiallyDeferred,
        _                    => throw new InvalidOperationException(PgStrings.UnknownDeferral(token)),
    };

    internal bool IsDeferrable => this is not NotDeferrableCase;

    internal bool IsInitiallyDeferred => this is InitiallyDeferredCase;

    /// <summary>The case's name, for messages.</summary>
    public sealed override string ToString() => Token;
}
