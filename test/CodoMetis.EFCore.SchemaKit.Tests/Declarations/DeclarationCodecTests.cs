using System.Reflection;
using System.Text.Json.Nodes;
using CodoMetis.EFCore.SchemaKit.Declarations.Internal;
using CodoMetis.EFCore.SchemaKit.Tests.Harness;

namespace CodoMetis.EFCore.SchemaKit.Tests.Declarations;

/// <summary>
/// The stored form of a declaration and the rule that keeps it honest: a value that goes into a
/// snapshot comes out of it the same, and what cannot be read as it was written is refused.
/// </summary>
[TestClass]
public sealed class DeclarationCodecTests
{
    /// <summary>A declaration made of one function, for the writer's own rules.</summary>
    private sealed record Written(Action<SchemaJsonWriter> Content) : ISchemaObject
    {
        public string Owner => "Test.Written";
        public string Kind => "written";
        public string? Name => null;
        public int Epoch => 1;
        public void Write(SchemaJsonWriter writer) => Content(writer);
    }

    private static string Json(Action<SchemaJsonWriter> content) => DeclarationCodec.Value(new Written(content));

    private static TestGuard RoundTrip(TestGuard declaration)
        => DeclarationCodec.Read<TestGuard>(DeclarationCodec.Key(declaration), DeclarationCodec.Value(declaration));

    private static string Edit(TestGuard declaration, Action<JsonObject> edit)
    {
        var json = JsonNode.Parse(DeclarationCodec.Value(declaration))!.AsObject();
        edit(json);
        return json.ToJsonString();
    }

    public static IEnumerable<object[]> Declarations =>
    [
        [new TestGuard.Unique([new GuardKey("Email")])],
        [new TestGuard.Unique([new GuardKey("Email")]) { NullsDistinct = false }],
        [new TestGuard.Unique([new GuardKey("Email", Descending: true), new GuardKey("Name")]) { Name = "ux", Epoch = 2 }],
        [new TestGuard.Unique([]) { Include = ["A", "B"], FillFactor = 0 }],
        [new TestGuard.Unique([new GuardKey("A")]) { FillFactor = 70, Concurrently = true }],
        [new TestGuard.Unique([new GuardKey("A")]) { Where = new GuardFilter("{A} <> 'it''s' AND \"B\" && {C}") }],
        [new TestGuard.Unique([new GuardKey("A")]) { Name = "naïve \"name\" \\ 名前", Where = new GuardFilter("") }],
        [new TestGuard.Unique([new GuardKey("A")]) { Where = new GuardFilter("\u0001\t\u007F\u2028\u0378\uA7DB\U0001F600") }],
        [new TestGuard.Trigger("Path")],
        [new TestGuard.Trigger("Path") { Name = "tr_path", Epoch = 7 }],
    ];

    [TestMethod]
    [DynamicData(nameof(Declarations))]
    public void A_declaration_reads_back_as_the_value_it_was(TestGuard declaration)
    {
        var read = RoundTrip(declaration);

        Assert.AreEqual(declaration, read);
        Assert.AreEqual(DeclarationCodec.Value(declaration), DeclarationCodec.Value(read), "Storing what was read must write the same text, or every Redeclare would rewrite the snapshot.");
        Assert.AreEqual(DeclarationCodec.Key(declaration), DeclarationCodec.Key(read));
    }

    // ---- the omitted-default rule ---------------------------------------------------------------------

    /// <summary>
    /// The prototype wrote with "omit defaults" and lost <c>"nullsDistinct":false</c>; its reader
    /// took the constructor's default, <c>true</c>, and a NULLS NOT DISTINCT index came back as a
    /// plain unique one. Here nothing is omitted but a null, so every value that is a default
    /// somewhere has to be in the text.
    /// </summary>
    [TestMethod]
    public void A_value_that_is_its_types_default_is_written()
    {
        var json = DeclarationCodec.Value(new TestGuard.Unique([new GuardKey("A")]) { NullsDistinct = false, FillFactor = 0 });

        StringAssert.Contains(json, "\"nullsDistinct\":false");
        StringAssert.Contains(json, "\"descending\":false");
        StringAssert.Contains(json, "\"include\":[]");
        StringAssert.Contains(json, "\"fillFactor\":0");
        StringAssert.Contains(json, "\"concurrently\":false");
    }

    /// <summary>
    /// Held for the writer as a whole, so that it stays true of a method added later: every method
    /// that is not <c>Optional…</c> writes its property when handed the default of its type.
    /// </summary>
    [TestMethod]
    public void Every_writer_method_that_is_not_optional_writes_a_default()
    {
        var methods = typeof(SchemaJsonWriter).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && !m.Name.StartsWith("Optional", StringComparison.Ordinal))
            .ToList();
        Assert.IsTrue(methods.Count >= 6, "Found almost no writer methods: the check would pass on nothing.");

        foreach (var method in methods)
        {
            var json = Json(writer =>
            {
                var generic = method.IsGenericMethodDefinition ? method.MakeGenericMethod(typeof(string)) : method;
                var arguments = generic.GetParameters().Select(p => p.Name == "name" ? "p" : Default(p.ParameterType)).ToArray();
                generic.Invoke(writer, arguments);
            });

            StringAssert.Contains(json, "\"p\":", $"SchemaJsonWriter.{method.Name} left out a default value.");
        }

        static object Default(Type type)
            => type == typeof(string) ? ""
                : type == typeof(IEnumerable<string>) ? Array.Empty<string>()
                : type == typeof(Action<SchemaJsonWriter>) ? new Action<SchemaJsonWriter>(_ => { })
                : type == typeof(Action<SchemaJsonWriter, string>) ? new Action<SchemaJsonWriter, string>((_, _) => { })
                : Activator.CreateInstance(type)!;
    }

    [TestMethod]
    public void An_optional_writer_method_writes_nothing_for_null_and_the_value_otherwise()
    {
        Assert.AreEqual("""{"$kind":"written","v":2,"r":1}""", Json(w =>
        {
            w.OptionalString("s", null);
            w.OptionalBoolean("b", null);
            w.OptionalInt32("i", null);
            w.OptionalObject<string>("o", null, (_, _) => { });
        }));
        Assert.AreEqual("""{"$kind":"written","v":2,"r":1,"s":"","b":false,"i":0,"o":{}}""", Json(w =>
        {
            w.OptionalString("s", "");
            w.OptionalBoolean("b", false);
            w.OptionalInt32("i", 0);
            w.OptionalObject("o", "", (_, _) => { });
        }));
    }

    /// <summary>The reader has a method for each of the writer's, under the same name: nothing can be written that cannot be read.</summary>
    [TestMethod]
    public void The_reader_has_a_method_for_every_method_of_the_writer()
    {
        static List<string> Names(Type type) => [.. type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName).Select(m => m.Name).Order(StringComparer.Ordinal)];

        CollectionAssert.AreEqual(Names(typeof(SchemaJsonWriter)), Names(typeof(SchemaJsonReader)));
    }

    [TestMethod]
    public void A_property_that_was_not_written_is_not_given_a_default()
    {
        var declaration = new TestGuard.Unique([new GuardKey("A")]) { NullsDistinct = false };
        var edited = Edit(declaration, json => json.Remove("nullsDistinct"));

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => DeclarationCodec.Read<TestGuard>("CodoMetis:Test.Guard:0", edited));

        StringAssert.Contains(error.Message, "has no 'nullsDistinct'");
    }

    [TestMethod]
    public void An_optional_property_that_was_not_written_reads_as_null()
    {
        var read = (TestGuard.Unique)RoundTrip(new TestGuard.Unique([new GuardKey("A")]));

        Assert.IsNull(read.FillFactor);
        Assert.IsNull(read.Where);
        Assert.IsNull(read.Name);
    }

    // ---- what cannot be read as it was written is refused ---------------------------------------------

    [TestMethod]
    public void A_property_the_declaration_does_not_read_is_refused()
    {
        var declaration = new TestGuard.Trigger("Path");

        var top = Assert.ThrowsExactly<InvalidOperationException>(
            () => DeclarationCodec.Read<TestGuard>("K", Edit(declaration, json => json["deferrable"] = true)));
        StringAssert.Contains(top.Message, "a property 'deferrable' that the package owning it does not read");

        var nested = Assert.ThrowsExactly<InvalidOperationException>(
            () => DeclarationCodec.Read<TestGuard>("K", Edit(
                new TestGuard.Unique([new GuardKey("A")]),
                json => json["keys"]![0]!["opclass"] = "text_pattern_ops")));
        StringAssert.Contains(nested.Message, "'K.keys[0]' has a property 'opclass'");
    }

    [TestMethod]
    [DataRow(3), DataRow(1), DataRow(null)]
    public void A_format_version_this_version_does_not_know_is_refused(int? version)
    {
        var edited = Edit(new TestGuard.Trigger("Path"), json =>
        {
            if (version is null) json.Remove("v"); else json["v"] = version;
        });

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => DeclarationCodec.Read<TestGuard>("K", edited));

        StringAssert.Contains(error.Message, $"has the format version {version?.ToString() ?? "none"}; this version of SchemaKit reads 2");
    }

    [TestMethod]
    [DataRow("not json"), DataRow("[]"), DataRow("\"text\""), DataRow("")]
    public void A_value_that_is_not_a_json_object_is_refused(string value)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => DeclarationCodec.Read<TestGuard>("K", value));

        StringAssert.Contains(error.Message, "is not a SchemaKit declaration");
    }

    [TestMethod]
    public void A_property_of_the_wrong_type_is_refused()
    {
        var declaration = new TestGuard.Unique([new GuardKey("A")]);

        StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => DeclarationCodec.Read<TestGuard>("K", Edit(declaration, j => j["nullsDistinct"] = "no"))).Message,
            "'nullsDistinct' that is not true or false");
        StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => DeclarationCodec.Read<TestGuard>("K", Edit(declaration, j => j["fillFactor"] = 1.5))).Message,
            "'fillFactor' that is not a whole number");
        StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => DeclarationCodec.Read<TestGuard>("K", Edit(declaration, j => j["include"] = new JsonArray(1)))).Message,
            "'include' that is not a list of strings");
        StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => DeclarationCodec.Read<TestGuard>("K", Edit(declaration, j => j["where"] = null))).Message,
            "'where' that is not an object");
    }

    // ---- the writer's own rules ------------------------------------------------------------------------

    [TestMethod]
    [DataRow("$kind"), DataRow("v"), DataRow("r"), DataRow("name")]
    public void A_declaration_cannot_write_a_property_the_kernel_writes(string name)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => Json(w => w.String(name, "x")));

        StringAssert.Contains(error.Message, $"wrote a property named '{name}'");
    }

    [TestMethod]
    public void A_nested_object_may_use_the_names_the_kernel_reserves_at_the_top()
        => Assert.AreEqual(
            """{"$kind":"written","v":2,"r":1,"key":{"name":"x","v":1}}""",
            Json(w => w.Object("key", k => { k.String("name", "x"); k.Int32("v", 1); })));

    [TestMethod]
    public void A_property_cannot_be_written_twice()
    {
        StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => Json(w => { w.String("a", "1"); w.Boolean("a", true); })).Message,
            "wrote the property 'a' twice");
        StringAssert.Contains(
            Assert.ThrowsExactly<InvalidOperationException>(() => Json(w => { w.String("a", "1"); w.Operational.String("a", "2"); })).Message,
            "wrote the property 'a' twice");
    }

    [TestMethod]
    public void An_operator_stays_readable_in_the_stored_text()
        => Assert.AreEqual(
            """{"$kind":"written","v":2,"r":1,"op":"&&","text":"a < b > 'c' + \"d\""}""",
            Json(w => { w.String("op", "&&"); w.String("text", "a < b > 'c' + \"d\""); }));

    // ---- strings are written by one rule, on every runtime ---------------------------------------------

    [TestMethod]
    public void A_string_is_escaped_where_json_requires_it_and_nowhere_else()
    {
        static string Text(string value) => CanonicalJson.Quote(value, "p");

        Assert.AreEqual("\"a \\\" b \\\\ c\"", Text("a \" b \\ c"));
        Assert.AreEqual("\"\\b\\t\\n\\f\\r\"", Text("\b\t\n\f\r"));
        Assert.AreEqual("\"\\u0000\\u0001\\u000B\\u001F\"", Text("\0\u0001\u000B\u001F"));
        Assert.AreEqual("\" /<>&'+`\u007F\u0084\u0086\u00A0\u2027\u202A\uFEFF\uFFFF\"", Text(" /<>&'+`\u007F\u0084\u0086\u00A0\u2027\u202A\uFEFF\uFFFF"));
        Assert.AreEqual("\"\\u0085\\u2028\\u2029\"", Text("\u0085\u2028\u2029"), "C#'s other line terminators: EF Core writes the value into a string literal in the snapshot and does not escape them.");
        Assert.AreEqual("\"\U0001F600\U0010FFFF\"", Text("\U0001F600\U0010FFFF"));
    }

    /// <summary>
    /// The runtime's encoders escape what their Unicode tables do not know as assigned, so the
    /// text they write for one declaration changes with the runtime, and the key with it: .NET 9
    /// escaped 22 characters that .NET 10 writes as themselves. Here every character of the BMP is
    /// written as itself, the unassigned ones included, on every path a string takes into the
    /// stored form. The five the rule escapes above U+001F are in the test before this one.
    /// </summary>
    [TestMethod]
    public void Every_character_is_written_as_itself_whatever_the_runtime_knows_of_it()
    {
        var all = string.Concat(Enumerable.Range(0x20, 0x10000 - 0x20)
            .Where(c => c is not ('"' or '\\' or 0x85 or 0x2028 or 0x2029) && !char.IsSurrogate((char)c))
            .Select(c => (char)c)) + "\U0001F600";

        Assert.AreEqual(
            $$"""{"$kind":"written","v":2,"r":1,"a":"{{all}}","b":"{{all}}","c":["{{all}}"]}""",
            Json(w =>
            {
                w.String("a", all);
                w.OptionalString("b", all);
                w.Strings("c", [all]);
            }));

        // The kind and the name, which the kernel writes. U+0378 has never been assigned.
        var kind = "k\u0378\U0001F600";
        var shaped = new Shaped("A", kind, all, 1);
        Assert.AreEqual($$"""{"$kind":"{{kind}}","v":2,"r":1,"name":"{{all}}"}""", DeclarationCodec.Value(shaped));
        Assert.AreEqual(shaped.Name, JsonNode.Parse(DeclarationCodec.Value(shaped))!["name"]!.GetValue<string>(), "What was written must read back as the string it was.");
    }

    [TestMethod]
    public void A_string_that_is_not_well_formed_is_refused()
    {
        // Built here and not in a DataRow: an attribute's argument is stored as UTF-8, which has no
        // form for half a surrogate pair, so the test would be handed U+FFFD.
        foreach (var text in new[] { "\uD83D", "a\uDE00b", "\uDE00\uD83D", "ok \uD83D", "\uD83D\uD83D\uDE00" })
        {
            foreach (var write in new Action<SchemaJsonWriter>[]
                     {
                         w => w.String("p", text),
                         w => w.OptionalString("p", text),
                         w => w.Strings("p", ["fine", text]),
                     })
            {
                var error = Assert.ThrowsExactly<InvalidOperationException>(() => Json(write));
                StringAssert.Contains(error.Message, "'p' is not well-formed UTF-16");
            }

            // A name is hashed and not written for its key, so it is the value that refuses it; Declare asks for both.
            StringAssert.Contains(
                Assert.ThrowsExactly<InvalidOperationException>(() => DeclarationCodec.Value(new Shaped("A", "k", text, 1))).Message,
                "'name' is not well-formed UTF-16");
        }
    }

    [TestMethod]
    [DataRow("a b"), DataRow("a.b"), DataRow("größe"), DataRow("$x"), DataRow("a\"b"), DataRow("a-b")]
    public void A_property_name_is_made_of_ascii_letters_digits_and_underscores(string name)
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => Json(w => w.Boolean(name, true)));
        StringAssert.Contains(error.Message, $"wrote a property named '{name}'. A property name is made of ASCII letters, digits and '_'");

        var nested = Assert.ThrowsExactly<InvalidOperationException>(() => Json(w => w.Object("o", o => o.Boolean(name, true))));
        StringAssert.Contains(nested.Message, "A property name is made of ASCII letters, digits and '_'");

        Assert.AreEqual("""{"$kind":"written","v":2,"r":1,"aZ_09":true}""", Json(w => w.Boolean("aZ_09", true)));
    }

    // ---- what a declaration must be --------------------------------------------------------------------

    private sealed record Shaped(string Owner, string Kind, string? Name, int Epoch) : ISchemaObject
    {
        public void Write(SchemaJsonWriter writer) { }
    }

    [TestMethod]
    [DataRow("", "k", null, 1, "has the owner ''")]
    [DataRow("A:B", "k", null, 1, "has the owner 'A:B'")]
    [DataRow("A B", "k", null, 1, "has the owner 'A B'")]
    [DataRow("A", "", null, 1, "has no kind")]
    [DataRow("A", "k", "", 1, "has an empty name")]
    [DataRow("A", "k", null, 0, "has the resolution epoch 0")]
    public void A_declaration_that_cannot_be_stored_is_refused(string owner, string kind, string? name, int epoch, string message)
    {
        var declaration = new Shaped(owner, kind, name, epoch);

        StringAssert.Contains(Assert.ThrowsExactly<InvalidOperationException>(() => DeclarationCodec.Key(declaration)).Message, message);
        StringAssert.Contains(Assert.ThrowsExactly<InvalidOperationException>(() => DeclarationCodec.Value(declaration)).Message, message);
    }

    /// <summary>A reader that drops the header's name or epoch would store another declaration the next time.</summary>
    private sealed record Forgetful(string Column) : ISchemaDeclaration<Forgetful>
    {
        public static string OwnerId => "Test.Forgetful";
        public string Owner => OwnerId;
        public string Kind => "forgetful";
        public string? Name { get; init; }
        public int Epoch { get; init; } = 1;
        public void Write(SchemaJsonWriter writer) => writer.String("column", Column);
        public static Forgetful Read(SchemaDeclarationHeader header, SchemaJsonReader reader) => new(reader.String("column"));
    }

    [TestMethod]
    public void A_reader_that_loses_the_name_or_the_epoch_is_refused()
    {
        var plain = new Forgetful("A");
        Assert.AreEqual(plain, DeclarationCodec.Read<Forgetful>("K", DeclarationCodec.Value(plain)));

        foreach (var declaration in new[] { plain with { Name = "n" }, plain with { Epoch = 2 } })
        {
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => DeclarationCodec.Read<Forgetful>("K", DeclarationCodec.Value(declaration)));
            StringAssert.Contains(error.Message, "whose kind, name or epoch is not the one that was stored");
        }
    }

    /// <summary>A union whose reader answers one case whatever the header says.</summary>
    private sealed record Turncoat(string Column, bool Other = false) : ISchemaDeclaration<Turncoat>
    {
        public static string OwnerId => "Test.Turncoat";
        public string Owner => OwnerId;
        public string Kind => Other ? "other" : "one";
        public string? Name => null;
        public int Epoch => 1;
        public void Write(SchemaJsonWriter writer) => writer.String("column", Column);
        public static Turncoat Read(SchemaDeclarationHeader header, SchemaJsonReader reader) => new(reader.String("column"));
    }

    [TestMethod]
    public void A_reader_that_returns_another_kind_is_refused()
    {
        var one = new Turncoat("A");
        Assert.AreEqual(one, DeclarationCodec.Read<Turncoat>("K", DeclarationCodec.Value(one)));

        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => DeclarationCodec.Read<Turncoat>("K", DeclarationCodec.Value(one with { Other = true })));

        StringAssert.Contains(error.Message, "whose kind, name or epoch is not the one that was stored");
    }

    /// <summary>
    /// A reader that takes a property and drops it. The reader has nothing unread, the header is
    /// intact, and the declaration is another one: the draft's lost unique index, on the way out
    /// of a snapshot.
    /// </summary>
    private sealed record Lossy(string Column, bool Unique = false) : ISchemaDeclaration<Lossy>
    {
        public static string OwnerId => "Test.Lossy";
        public string Owner => OwnerId;
        public string Kind => "lossy";
        public string? Name => null;
        public int Epoch => 1;

        public void Write(SchemaJsonWriter writer)
        {
            writer.String("column", Column);
            writer.Boolean("unique", Unique);
        }

        public static Lossy Read(SchemaDeclarationHeader header, SchemaJsonReader reader)
        {
            var column = reader.String("column");
            _ = reader.Boolean("unique");
            return new Lossy(column);
        }
    }

    [TestMethod]
    public void A_reader_that_drops_a_property_it_read_is_refused()
    {
        var plain = new Lossy("Code");
        var unique = new Lossy("Code", Unique: true);
        Assert.AreEqual(plain, DeclarationCodec.Read<Lossy>(DeclarationCodec.Key(plain), DeclarationCodec.Value(plain)));

        var key = DeclarationCodec.Key(unique);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => DeclarationCodec.Read<Lossy>(key, DeclarationCodec.Value(unique)));

        StringAssert.Contains(error.Message, $"Lossy.Read built a declaration for the annotation '{key}' that is not the one that was stored");
        StringAssert.Contains(error.Message, "\"unique\":true");
        StringAssert.Contains(error.Message, "\"unique\":false");
    }

    [TestMethod]
    public void A_stored_declaration_that_could_not_have_been_stored_is_refused()
    {
        var edited = Edit(new TestGuard.Trigger("Path"), json => json["r"] = 0);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => DeclarationCodec.Read<TestGuard>("K", edited));

        StringAssert.Contains(error.Message, "has the resolution epoch 0");
    }

    // ---- what is operational stays out of the identity ---------------------------------------------------

    /// <summary>The same inside a nested object that is itself part of the identity: a key column's build option, say.</summary>
    [TestMethod]
    public void An_operational_property_of_a_nested_object_is_stored_and_stays_out_of_the_key()
    {
        static Written Key(bool? option) => new(w => w.Object("key", k =>
        {
            k.String("column", "A");
            k.Operational.OptionalBoolean("option", option);
        }));

        StringAssert.Contains(DeclarationCodec.Value(Key(true)), """{"column":"A","option":true}""");
        Assert.AreEqual(DeclarationCodec.Key(Key(null)), DeclarationCodec.Key(Key(true)));
    }

    /// <summary>
    /// Held for the writer as a whole, like the omitted-default rule: whatever method writes it,
    /// an operational property is in the stored value and not in the key. A method that wrote one
    /// into the identity would change the key of every declaration using it, and the key is permanent.
    /// </summary>
    [TestMethod]
    public void Every_writer_method_keeps_an_operational_property_out_of_the_key()
    {
        var methods = typeof(SchemaJsonWriter).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToList();
        Assert.IsTrue(methods.Count >= 9, "Found almost no writer methods: the check would pass on nothing.");

        var empty = DeclarationCodec.Key(new Written(_ => { }));

        foreach (var method in methods)
        {
            var declaration = new Written(writer =>
            {
                var generic = method.IsGenericMethodDefinition ? method.MakeGenericMethod(typeof(string)) : method;
                var arguments = generic.GetParameters().Select(p => p.Name == "name" ? "p" : Value(p.ParameterType)).ToArray();
                generic.Invoke(writer.Operational, arguments);
            });

            StringAssert.Contains(DeclarationCodec.Value(declaration), "\"p\":", $"SchemaJsonWriter.{method.Name} did not store an operational property.");
            Assert.AreEqual(empty, DeclarationCodec.Key(declaration), $"SchemaJsonWriter.{method.Name} wrote an operational property into the key.");
        }

        // Something in every position, nested ones included: a null or an empty list would be left out anyway.
        static object Value(Type type)
            => type == typeof(string) ? "x"
                : type == typeof(bool) || type == typeof(bool?) ? true
                : type == typeof(int) || type == typeof(int?) ? 1
                : type == typeof(IEnumerable<string>) ? new[] { "x" }
                : type == typeof(Action<SchemaJsonWriter>) ? new Action<SchemaJsonWriter>(w => w.String("inner", "x"))
                : type == typeof(Action<SchemaJsonWriter, string>) ? new Action<SchemaJsonWriter, string>((w, item) => w.String("inner", item))
                : throw new NotSupportedException($"No value for a parameter of type {type}: add one, so the new writer method is covered.");
    }
}
