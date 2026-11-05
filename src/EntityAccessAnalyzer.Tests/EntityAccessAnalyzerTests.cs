using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace EntityAccessAnalyzer.Tests;

public class EntityAccessAnalyzerTests
{
    [Theory]
    [InlineData("public class Entity { public int Value; }", "EAA0001")]
    [InlineData("public struct Entity { public static int Value; }", "EAA0001")]
    [InlineData("public class Entity { public const int Value = 1; public readonly int Other; }", "")]
    [InlineData("public class Entity { public int Value { get; set; } }", "EAA0001")]
    [InlineData("public class Entity { public int Value { get; init; } }", "")]
    [InlineData("public class Entity { public int Value { get; internal set; } }", "")]
    [InlineData("public class Entity { public int this[int i] { get => 0; set {} } }", "EAA0001")]
    [InlineData("public class Entity { private int value; public ref int Value => ref value; }", "EAA0001")]
    [InlineData("public class Entity { private int value; public ref readonly int Value => ref value; }", "")]
    [InlineData("public class Entity { private int value; public ref int Get() => ref value; }", "EAA0001 EAA0002")]
    [InlineData("public class Entity { private int value; public ref readonly int Get() => ref value; }", "EAA0002")]
    [InlineData("public ref struct Entity { public ref int Value; }", "EAA0001")]
    [InlineData("public ref struct Entity { public readonly ref int Value; }", "EAA0001")]
    [InlineData("public ref struct Entity { public ref readonly int Value; }", "EAA0001")]
    [InlineData("public ref struct Entity { public readonly ref readonly int Value; }", "")]
    [InlineData("public class Entity { public event Action Changed; public event Action Other { add {} remove {} } }", "")]
    [InlineData("public class Entity { public Entity(out List<int> values) { values = null; } public List<int> Values { private get; init; } }", "")]
    [InlineData("public class Entity { public void Change() {} }", "EAA0002")]
    [InlineData("public class Entity { public static Entity Create() => null; }", "EAA0002")]
    [InlineData("public class Entity { public bool Equals(Entity other) => true; }", "EAA0002")]
    [InlineData("public class Entity : IEquatable<Entity> { bool IEquatable<Entity>.Equals(Entity other) => true; public bool Equals(string other) => true; }", "EAA0002")]
    [InlineData("public class Entity { public static Entity Parse(string s, IFormatProvider p) => null; public static bool TryParse(string s, IFormatProvider p, out Entity value) { value = null; return true; } }", "EAA0002 EAA0002")]
    [InlineData("public class Entity { public string ToString(string format, IFormatProvider provider) => null; public int GetHashCode(Entity other) => 0; public int Compare(Entity x, Entity y) => 0; }", "EAA0002 EAA0002 EAA0002")]
    [InlineData("public class Entity { public int CompareTo(Entity other) => 0; }", "EAA0002")]
    [InlineData("public class Entity : IEquatable<Entity> { public bool Equals(Entity other) => true; }", "")]
    [InlineData("public class Entity : IComparable<Entity>, IComparable { public int CompareTo(Entity other) => 0; public int CompareTo(object other) => 0; }", "")]
    [InlineData("public class Entity : IComparer<Entity>, IComparer { public int Compare(Entity x, Entity y) => 0; public int Compare(object x, object y) => 0; }", "")]
    [InlineData("public class Entity : IEqualityComparer<Entity>, IEqualityComparer { public bool Equals(Entity x, Entity y) => true; public int GetHashCode(Entity x) => 0; public new bool Equals(object x, object y) => true; public int GetHashCode(object x) => 0; }", "")]
    [InlineData("public class Entity : IStructuralComparable, IStructuralEquatable { public int CompareTo(object x, IComparer c) => 0; public bool Equals(object x, IEqualityComparer c) => true; public int GetHashCode(IEqualityComparer c) => 0; }", "")]
    [InlineData("public class Entity : ISpanFormattable { public string ToString(string f, IFormatProvider p) => null; public bool TryFormat(Span<char> d, out int w, ReadOnlySpan<char> f, IFormatProvider p) { w = 0; return true; } }", "")]
    [InlineData("public class Entity : IUtf8SpanFormattable { public bool TryFormat(Span<byte> d, out int w, ReadOnlySpan<char> f, IFormatProvider p) { w = 0; return true; } }", "")]
    [InlineData("public class Entity : IParsable<Entity> { public static Entity Parse(string s, IFormatProvider p) => null; public static bool TryParse(string s, IFormatProvider p, out Entity r) { r = null; return true; } }", "")]
    [InlineData("public class Entity : ISpanParsable<Entity> { public static Entity Parse(string s, IFormatProvider p) => null; public static bool TryParse(string s, IFormatProvider p, out Entity r) { r = null; return true; } public static Entity Parse(ReadOnlySpan<char> s, IFormatProvider p) => null; public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider p, out Entity r) { r = null; return true; } }", "")]
    [InlineData("public class Entity : IUtf8SpanParsable<Entity> { public static Entity Parse(ReadOnlySpan<byte> s, IFormatProvider p) => null; public static bool TryParse(ReadOnlySpan<byte> s, IFormatProvider p, out Entity r) { r = null; return true; } }", "")]
    [InlineData("public class Entity { public override string ToString() => null; public override bool Equals(object other) => true; public override int GetHashCode() => 0; }", "")]
    [InlineData("public class Entity { public new string ToString() => null; public new bool Equals(object other) => true; public new int GetHashCode() => 0; }", "EAA0002 EAA0002 EAA0002")]
    [InlineData("public class Entity : IDisposable { public void Dispose() {} }", "EAA0002")]
    [InlineData("public class Entity : IAsyncDisposable { public ValueTask DisposeAsync() => default; }", "EAA0002")]
    [InlineData("public class Entity : ICloneable { public object Clone() => null; }", "EAA0002")]
    [InlineData("public class Entity : IDisposable, ICloneable, IAsyncDisposable { void IDisposable.Dispose() {} object ICloneable.Clone() => null; ValueTask IAsyncDisposable.DisposeAsync() => default; }", "")]
    [InlineData("public class Entity { public static Entity operator +(Entity x, Entity y) => x; public static Entity operator ++(Entity x) => x; }", "EAA0002 EAA0002")]
    [InlineData("public class Entity { public static bool operator ==(Entity x, Entity y) => true; public static bool operator !=(Entity x, Entity y) => false; public static bool operator <(Entity x, Entity y) => true; public static bool operator >(Entity x, Entity y) => false; public static bool operator <=(Entity x, Entity y) => true; public static bool operator >=(Entity x, Entity y) => false; }", "")]
    [InlineData("public class Entity { public static implicit operator int(Entity x) => 0; public static explicit operator List<int>(Entity x) => null; }", "EAA0003")]
    [InlineData("public class Entity { public void Deconstruct(out int x) { x = 0; } }", "")]
    [InlineData("public class Entity { public static void Deconstruct(out int x) { x = 0; } }", "EAA0002")]
    [InlineData("public class Entity { public void Deconstruct() {} }", "EAA0002")]
    [InlineData("public class Entity { public int Deconstruct(out int x) { x = 0; return 0; } }", "EAA0002")]
    [InlineData("public class Entity { public void Deconstruct(ref int x) {} }", "EAA0002")]
    [InlineData("public class Entity { public void Deconstruct<T>(out T x) { x = default; } }", "")]
    [InlineData("public class Entity { public void Deconstruct<T>(out T x) where T : ICollection<int> { x = default; } }", "EAA0003")]
    [InlineData("public class Entity { public void Deconstruct<T>(out T x) where T : IEnumerable<int> { x = default; } }", "EAA0004")]
    [InlineData("public class Entity { public void Deconstruct(out int x, int y) { x = 0; } }", "EAA0002")]
    [InlineData("public class Entity { public void Deconstruct(out List<int> x, out IEnumerable<int> y) { x = null; y = null; } }", "EAA0003 EAA0004")]
    [InlineData("public class Entity { public void Deconstruct(out List<int> x, out List<int> y) { x = null; y = null; } }", "EAA0003")]
    [InlineData("public record Entity(int Id);", "")]
    [InlineData("public readonly record struct Entity(int Id);", "")]
    [InlineData("public record struct Entity(int Id);", "EAA0001")]
    [InlineData("public record Entity(List<int> Values);", "EAA0003 EAA0003")]
    [InlineData("public class Entity { public List<int> Values; }", "EAA0001 EAA0003")]
    [InlineData("public class Entity { public readonly List<int> Values; }", "EAA0003")]
    [InlineData("public class Entity { public int[] Values => null; }", "EAA0003")]
    [InlineData("public class Entity { public int[,] Values => null; }", "EAA0003")]
    [InlineData("public class Entity { public ArrayList Values => null; public Hashtable Other => null; }", "EAA0003 EAA0003")]
    [InlineData("public class Entity { public ICollection Values => null; public ICollection<int> Other => null; public IList<int> More => null; }", "EAA0003 EAA0003 EAA0003")]
    [InlineData("public class Entity { public Memory<int> Values => default; public Span<int> Other => default; public ArraySegment<int> More => default; }", "EAA0003 EAA0003 EAA0003")]
    [InlineData("public class Entity { public Action A => null; public Func<int> F => null; public Callback C => null; } public delegate void Callback();", "EAA0003 EAA0003 EAA0003")]
    [InlineData("public class Entity { public IEnumerable Values => null; public IEnumerable<int> Other => null; public IAsyncEnumerable<int> More => null; }", "EAA0004 EAA0004 EAA0004")]
    [InlineData("public class Entity { public IReadOnlyList<List<int>> Values => null; }", "EAA0003")]
    [InlineData("public class Entity { public IReadOnlyDictionary<List<int>, IEnumerable<int>> Values => null; }", "EAA0003 EAA0004")]
    [InlineData("public class Entity { public (List<int> A, List<int> B, IEnumerable<int> C) Values => default; }", "EAA0003 EAA0004")]
    [InlineData("public class Entity { public (int, int, int, int, int, int, int, List<int>, IEnumerable<int>) Values => default; }", "EAA0003 EAA0004")]
    [InlineData("public class Entity { public ReadOnlyMemory<List<int>> Values => default; public ReadOnlySpan<IEnumerable<int>> Other => default; }", "EAA0003 EAA0004")]
    [InlineData("public class Entity { public ImmutableArray<List<int>> Values => default; public ReadOnlySet<IEnumerable<int>> Other => null; }", "EAA0003 EAA0004")]
    [InlineData("public class Entity { public ImmutableList<int>.Builder Values => null; public ImmutableDictionary<int, int>.Builder Other => null; }", "EAA0003 EAA0003")]
    [InlineData("public class Entity { public List<int> Values { private get; set; } }", "EAA0001")]
    [InlineData("public class Entity { private List<int> value; public ref readonly List<int> Values => ref value; }", "EAA0003")]
    [InlineData("public class Entity { public List<int> this[List<int> input] => null; }", "EAA0003")]
    [InlineData("public class Entity { public Task<List<int>> Values => null; public ValueTask<IEnumerable<int>> Other => default; public ImmutableArray<List<int>>? More => default; }", "")]
    [InlineData("public unsafe class Entity { public int* Value => null; public delegate*<List<int>> Callback => null; }", "")]
    [InlineData("public class Entity<T> { public T Values => default; }", "")]
    [InlineData("public class Entity<T> where T : ICollection<int> { public T Values => default; }", "EAA0003")]
    [InlineData("public class Entity<T> where T : ICollection { public T Values => default; }", "EAA0003")]
    [InlineData("public class Entity<T> where T : IEnumerable { public T Values => default; }", "EAA0004")]
    [InlineData("public interface ISequence<T> : IEnumerable<T> {} public class Entity<T> where T : ISequence<int> { public T Values => default; }", "EAA0004")]
    [InlineData("public interface IView<T> : IReadOnlyList<T> {} public class Entity<T> where T : IView<List<int>> { public T Values => default; }", "")]
    [InlineData("public class Entity<T> where T : ICollection<int>, IReadOnlyList<int> { public T Values => default; }", "EAA0003")]
    [InlineData("public class Entity<T> where T : IEnumerable<int> { public T Values => default; }", "EAA0004")]
    [InlineData("public class Entity<T> where T : IAsyncEnumerable<int> { public T Values => default; }", "EAA0004")]
    [InlineData("public class Entity<T> where T : IEnumerable<int>, IReadOnlyList<int> { public T Values => default; }", "")]
    [InlineData("public class Entity<T> where T : IReadOnlyList<List<int>> { public T Values => default; }", "EAA0003")]
    [InlineData("public class Entity<T> where T : ReadOnlyCollection<int> { public T Values => default; }", "")]
    [InlineData("public class Entity<T> where T : ReadOnlyCollection<List<int>> { public T Values => default; }", "EAA0003")]
    [InlineData("public class Entity<T> where T : ReadOnlyCollection<int>, ICollection<int> { public T Values => default; }", "EAA0003")]
    [InlineData("public class Entity<T, U> where T : U where U : IReadOnlyList<IEnumerable<int>> { public T Values => default; }", "EAA0004")]
    [InlineData("public class Entity<T> where T : Delegate { public T Values => default; }", "EAA0003")]
    [InlineData("public class Entity<T> where T : MulticastDelegate { public T Values => default; }", "EAA0003")]
    [InlineData("public class Entity<T> where T : IReadOnlyList<T> { public T Values => default; }", "")]
    [InlineData("internal class Entity { public int Value; public void Change() {} }", "")]
    [InlineData("internal class Outer { public class Entity { public int Value; } }", "")]
    [InlineData("public class Outer { public class Entity { public int Value; } }", "EAA0001")]
    [InlineData("public class Outer { private class Entity { public int Value; } protected class Other { public void Change() {} } }", "")]
    [InlineData("public interface IEntity { int Value { get; set; } void Change() {} } public enum Kind { A } public delegate List<int> Callback();", "")]
    [InlineData("public interface IEntity { int Value { get; set; } } public class Entity : IEntity { int IEntity.Value { get; set; } }", "")]
    [InlineData("public interface IEntity { void Change() {} } public class Entity : IEntity {}", "")]
    [InlineData("public interface IEntity { void Change(); } public class Entity : IEntity { public void Change() {} }", "EAA0002")]
    [InlineData("public class Entity { internal List<int> Values; protected void Change() {} private int Value { get; set; } }", "")]
    [InlineData("public partial class Entity { public partial void Change(); } public partial class Entity { public partial void Change() {} }", "EAA0002")]
    public async Task PublicApiFollowsPolicy(string declaration, string expectedIds)
    {
        var source = """
            using System;
            using System.Collections;
            using System.Collections.Generic;
            using System.Collections.Immutable;
            using System.Collections.ObjectModel;
            using System.Threading.Tasks;
            [assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities")]
            namespace App.Entities;
            """ + "\n" + declaration;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "Consumer",
            new[] { CSharpSyntaxTree.ParseText(source, parseOptions, "Entity.cs") },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EntityAccessScopeGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        Assert.Empty(generatorDiagnostics);
        Assert.Empty(output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var diagnostics = await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
        Assert.Equal(
            expectedIds.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(static id => id),
            diagnostics.Select(static diagnostic => diagnostic.Id).OrderBy(static id => id));
        Assert.All(
            diagnostics,
            diagnostic =>
            {
                Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
                Assert.Equal("Entity.cs", diagnostic.Location.SourceTree?.FilePath);
                Assert.Contains("App.Entities.", diagnostic.GetMessage(CultureInfo.InvariantCulture));
            });
    }

    [Theory]
    [InlineData("", "App.Entities", false)]
    [InlineData("null", "App.Entities", false)]
    [InlineData("\"\"", "App.Entities", false)]
    [InlineData("new string[0]", "App.Entities", false)]
    [InlineData("\"App.Entities\"", "App.Entities", true)]
    [InlineData("\"App.Entities\"", "App.Entities.Child", false)]
    [InlineData("\"App.Entities.*\"", "App.Entities", false)]
    [InlineData("\"App.Entities.*\"", "App.Entities.Child", true)]
    [InlineData("\"App.Entities.*\"", "App.Entities.Child.Grandchild", true)]
    [InlineData("\"*.Entities\"", "App.Entities", true)]
    [InlineData("\"App.Entit?es\"", "App.Entities", true)]
    [InlineData("\"app.entities\"", "App.Entities", false)]
    [InlineData("null, \"\", \"Other\", \"App.Entities\"", "App.Entities", true)]
    [InlineData("\"*\"", "", false)]
    public async Task AssemblyPatternsSelectNamespaces(string arguments, string namespaceName, bool expected)
    {
        var source = "[assembly: EntityAccessAnalyzer.EntityAccessScope(" + arguments + ")]\n" +
            (namespaceName.Length > 0 ? "namespace " + namespaceName + ";\n" : "") + "public class Entity { public int Value; }";
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "Consumer",
            new[] { CSharpSyntaxTree.ParseText(source, parseOptions) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EntityAccessScopeGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        Assert.Empty(output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var diagnostics = await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
        Assert.Equal(expected ? new[] { "EAA0001" } : Array.Empty<string>(), diagnostics.Select(static diagnostic => diagnostic.Id));
    }

    [Theory]
    [InlineData("IReadOnlyCollection<int>")]
    [InlineData("IReadOnlyList<int>")]
    [InlineData("IReadOnlyDictionary<int, int>")]
    [InlineData("IReadOnlySet<int>")]
    [InlineData("ImmutableArray<int>")]
    [InlineData("ImmutableList<int>")]
    [InlineData("ImmutableHashSet<int>")]
    [InlineData("ImmutableSortedSet<int>")]
    [InlineData("ImmutableDictionary<int, int>")]
    [InlineData("ImmutableSortedDictionary<int, int>")]
    [InlineData("ImmutableQueue<int>")]
    [InlineData("ImmutableStack<int>")]
    [InlineData("IImmutableList<int>")]
    [InlineData("IImmutableSet<int>")]
    [InlineData("IImmutableDictionary<int, int>")]
    [InlineData("IImmutableQueue<int>")]
    [InlineData("IImmutableStack<int>")]
    [InlineData("FrozenSet<int>")]
    [InlineData("FrozenDictionary<int, int>")]
    [InlineData("ReadOnlyCollection<int>")]
    [InlineData("ReadOnlyDictionary<int, int>")]
    [InlineData("ReadOnlyObservableCollection<int>")]
    [InlineData("ReadOnlySet<int>")]
    [InlineData("ReadOnlyMemory<int>")]
    [InlineData("ReadOnlySpan<int>")]
    [InlineData("string")]
    public async Task StandardReadOnlyTypesInspectTheirElements(string type)
    {
        foreach (var elementType in new[] { "int", "List<int>", "IEnumerable<int>" })
        {
            var source = """
            using System;
            using System.Collections.Generic;
            using System.Collections.Immutable;
            using System.Collections.Frozen;
            using System.Collections.ObjectModel;
            [assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities")]
            namespace App.Entities;
            public class Entity
            {
            """ + "public " + type.Replace("int", elementType, StringComparison.Ordinal) + " Values => default; }";
            var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(static path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create(
                "Consumer",
                new[] { CSharpSyntaxTree.ParseText(source, parseOptions) },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                new[] { new EntityAccessScopeGenerator().AsSourceGenerator() },
                parseOptions: parseOptions);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
            Assert.Empty(output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
            var diagnostics = await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()))
                .GetAnalyzerDiagnosticsAsync();
            if (elementType == "int" || type == "string")
            {
                Assert.Empty(diagnostics);
            }
            else
            {
                var diagnostic = Assert.Single(diagnostics);
                Assert.Equal(elementType == "List<int>" ? "EAA0003" : "EAA0004", diagnostic.Id);
                Assert.Contains("'System.Collections.Generic." + elementType + "'", diagnostic.GetMessage(CultureInfo.InvariantCulture));
                Assert.Equal("Values", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
                Assert.Empty(diagnostic.AdditionalLocations);
            }
        }
    }

    [Theory]
    [InlineData("public int Value;", "", "EAA0001")]
    [InlineData("public int Value { get; set; }", "public new int Value => 0;", "")]
    [InlineData("public int Value;", "public new int Value => 0;", "")]
    [InlineData("public int Value;", "private new int Value => 0;", "EAA0001")]
    [InlineData("public int Value;", "internal new int Value => 0;", "EAA0001")]
    [InlineData("public void Change(int x) {}", "public new void Change(int x) {}", "EAA0002")]
    [InlineData("public void Change(string x) {}", "public void Change(int x) {}", "EAA0002 EAA0002")]
    [InlineData("public void Change<T>(T x) {}", "public new void Change<U>(U x) {}", "EAA0002")]
    [InlineData("public void Change<T>(System.Collections.Generic.List<T[]> x) {}", "public new void Change<U>(System.Collections.Generic.List<U[]> x) {}", "EAA0002")]
    [InlineData("public void Change(object x) {}", "public new void Change(dynamic x) {}", "EAA0002")]
    [InlineData("public void Change(ref int x) {}", "public new void Change(out int x) { x = 0; }", "EAA0002")]
    [InlineData("public void Change(ref int x) {}", "public void Change(int x) {}", "EAA0002 EAA0002")]
    [InlineData("public void Change<T>(T x) {}", "public void Change(int x) {}", "EAA0002 EAA0002")]
    [InlineData("public void Change() {}", "public new int Change => 0;", "")]
    [InlineData("public int Value;", "public new class Value {}", "")]
    [InlineData("public int Value;", "public new class Value<T> {}", "EAA0001")]
    [InlineData("public virtual void Change() {}", "public override void Change() {}", "EAA0002")]
    [InlineData("public int this[int x] { get => 0; set {} }", "public new int this[int x] => 0;", "")]
    [InlineData("public int this[string x] { get => 0; set {} }", "public int this[int x] => 0;", "EAA0001")]
    [InlineData("public int this[int x, int y] { get => 0; set {} }", "public int this[int x] => 0;", "EAA0001")]
    [InlineData("public int this[in int x] { get => 0; set {} }", "public int this[int x] => 0;", "EAA0001")]
    [InlineData("public virtual int Value { get; set; }", "public override int Value { get => 0; }", "EAA0001")]
    [InlineData("public virtual System.Collections.Generic.List<int> Value { get; set; }", "public override System.Collections.Generic.List<int> Value { set {} }", "EAA0001 EAA0003")]
    [InlineData("public static explicit operator System.Collections.Generic.List<int>(Base x) => null;", "public static explicit operator int(Entity x) => 0;", "EAA0003")]
    [InlineData("public void Deconstruct(out System.Collections.Generic.IEnumerable<int> x) { x = null; }", "", "EAA0004")]
    [InlineData("public virtual string ToString(string x) => x;", "", "EAA0002")]
    [InlineData("public override string ToString() => null;", "", "")]
    public async Task InheritedApiUsesCSharpHiding(string baseMembers, string entityMembers, string expectedIds)
    {
        var source = """
            [assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities")]
            namespace Other { public class Base {
            """ + baseMembers + "} }\nnamespace App.Entities { public class Entity : Other.Base {" + entityMembers + "} }";
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "Consumer",
            new[] { CSharpSyntaxTree.ParseText(source, parseOptions, "Entity.cs") },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EntityAccessScopeGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        Assert.Empty(output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var diagnostics = await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
        Assert.Equal(
            expectedIds.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(static id => id),
            diagnostics.Select(static diagnostic => diagnostic.Id).OrderBy(static id => id));
        foreach (var diagnostic in diagnostics.Where(static diagnostic => diagnostic.AdditionalLocations.Count > 0))
        {
            Assert.Equal("Other.Base", source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length));
            Assert.Single(diagnostic.AdditionalLocations);
            Assert.Contains("Other.Base.", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public async Task GeneratedAndPartialMembersReportOnEditableEntityOnce()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText(
                """
                // <auto-generated/>
                namespace App.Entities;
                public partial class Entity { public int Generated { get; set; } }
                """,
                parseOptions,
                "Entity.g.cs"),
            CSharpSyntaxTree.ParseText(
                """
                [assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities")]
                namespace App.Entities;
                public partial class Entity { public int Direct { get; set; } }
                """,
                parseOptions,
                "Entity.cs"),
            CSharpSyntaxTree.ParseText(
                """
                namespace App.Entities;
                public partial class Entity { public int Other { get; set; } }
                """,
                parseOptions,
                "Entity.Other.cs"),
        };
        var compilation = CSharpCompilation.Create(
            "Consumer",
            trees,
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EntityAccessScopeGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        Assert.Empty(output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var diagnostics = await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
        Assert.Equal(3, diagnostics.Length);
        var generated = Assert.Single(diagnostics.Where(static diagnostic => diagnostic.AdditionalLocations.Count > 0));
        Assert.Equal("Entity.cs", generated.Location.SourceTree?.FilePath);
        Assert.Equal("Entity", generated.Location.SourceTree!.GetText().ToString(generated.Location.SourceSpan));
        Assert.Equal("Entity.g.cs", Assert.Single(generated.AdditionalLocations).SourceTree?.FilePath);
        Assert.Contains("App.Entities.Entity.Generated", generated.GetMessage(CultureInfo.InvariantCulture));
        Assert.All(
            diagnostics.Where(static diagnostic => diagnostic.AdditionalLocations.Count == 0),
            diagnostic => Assert.Equal("set", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan)));
    }

    [Theory]
    [InlineData("public interface View<T> : IReadOnlyList<T> {}", "Other.View<List<int>>", "")]
    [InlineData("public interface Sequence<T> : IEnumerable<T> {}", "Other.Sequence<List<int>>", "")]
    [InlineData("public interface IReadOnlyList<T> : ICollection<T> {}", "Other.IReadOnlyList<int>", "EAA0003")]
    [InlineData("public class View<T> : List<T> {}", "Other.View<int>", "EAA0003")]
    [InlineData("public class View<T> : ReadOnlyCollection<T> { public View() : base(new List<T>()) {} }", "Other.View<int>", "EAA0003")]
    [InlineData("public class Address { public List<int> Values; public void Change() {} }", "Other.Address", "")]
    public async Task CustomTypesAreCheckedByTheirOwnContract(string customDeclaration, string exposedType, string expectedId)
    {
        var source = """
            using System.Collections.Generic;
            using System.Collections.ObjectModel;
            [assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities")]
            namespace Other {
            """ + customDeclaration + "}\nnamespace App.Entities { public class Entity { public " + exposedType + " Values => null; } }";
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "Consumer",
            new[] { CSharpSyntaxTree.ParseText(source, parseOptions) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EntityAccessScopeGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        Assert.Empty(output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var diagnostics = await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
        Assert.Equal(expectedId.Split(' ', StringSplitOptions.RemoveEmptyEntries), diagnostics.Select(static diagnostic => diagnostic.Id));
    }

    [Fact]
    public async Task MetadataBaseMembersUseConstructedTypesAndReportTheirOrigin()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path)).ToArray();
        var library = CSharpCompilation.Create(
            "BaseLibrary",
            new[]
            {
                CSharpSyntaxTree.ParseText(
                    """
                    using System;
                    using System.Collections.Generic;
                    namespace Other;
                    public class Base<T> : IComparable<T>
                    {
                        public T Value { get; set; }
                        public List<T> Values => null;
                        public virtual int CompareTo(T other) => 0;
                        public void Update<U>(U value) {}
                    }
                    """,
                    parseOptions)
            },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = library.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var compilation = CSharpCompilation.Create(
            "Consumer",
            new[]
            {
                CSharpSyntaxTree.ParseText(
                    """
                    [assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities")]
                    namespace App.Entities;
                    public class Entity : Other.Base<int>
                    {
                        public override int CompareTo(int other) => 0;
                        public new void Update<U>(U value) {}
                    }
                    """,
                    parseOptions,
                    "Entity.cs")
            },
            references.Append(MetadataReference.CreateFromImage(stream.ToArray())),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EntityAccessScopeGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        Assert.Empty(output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var diagnostics = await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
        Assert.Equal(new[] { "EAA0001", "EAA0002", "EAA0003" }, diagnostics.Select(static diagnostic => diagnostic.Id).OrderBy(static id => id));
        foreach (var diagnostic in diagnostics.Where(static diagnostic => diagnostic.Id != "EAA0002"))
        {
            Assert.Empty(diagnostic.AdditionalLocations);
            Assert.Equal("Other.Base<int>", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
            Assert.Contains("Other.Base<int>.", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        }
        Assert.Contains(
            "List<int>",
            Assert.Single(diagnostics.Where(static diagnostic => diagnostic.Id == "EAA0003"))
            .GetMessage(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task SeverityAndPragmaSuppressionAreRespected()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "Consumer",
            new[]
            {
                CSharpSyntaxTree.ParseText(
                    """
                    using System.Collections.Generic;
                    [assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities")]
                    namespace App.Entities;
                    public class Entity
                    {
                        public int Value;
                        public void Change() {}
                        #pragma warning disable EAA0003
                        public List<int> Values => null;
                        #pragma warning restore EAA0003
                        public List<int> Other => null;
                    }
                    """,
                    parseOptions)
            },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithSpecificDiagnosticOptions(
                ImmutableDictionary<string, ReportDiagnostic>.Empty
                .Add("EAA0001", ReportDiagnostic.Warn).Add("EAA0002", ReportDiagnostic.Suppress)));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EntityAccessScopeGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var diagnostics = await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
        Assert.Equal(2, diagnostics.Length);
        var write = Assert.Single(diagnostics.Where(static diagnostic => diagnostic.Id == "EAA0001"));
        Assert.Equal(DiagnosticSeverity.Warning, write.Severity);
        Assert.Contains("書き込み", write.GetMessage(CultureInfo.GetCultureInfo("ja-JP")));
        Assert.Contains("write access", write.GetMessage(CultureInfo.InvariantCulture));
        var prohibitedType = Assert.Single(diagnostics.Where(static diagnostic => diagnostic.Id == "EAA0003"));
        Assert.Contains("Entity.Other", prohibitedType.GetMessage(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task MissingConfigurationAndInvalidCodeDoNotCrash()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        foreach (var configuration in new[] { "", "[assembly: EntityAccessAnalyzer.EntityAccessScope(\"App.Entities\")]" })
        {
            var source = configuration + """

                namespace App.Entities;
                public class Entity<T, U> where T : U where U : T
                {
                    public Unknown Value => default;
                    public T Cycle => default;
                }
                """;
            var compilation = CSharpCompilation.Create(
                "Consumer",
                new[] { CSharpSyntaxTree.ParseText(source, parseOptions) },
                new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                new[] { new EntityAccessScopeGenerator().AsSourceGenerator() },
                parseOptions: parseOptions);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
            Assert.Empty(
                await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()))
                .GetAnalyzerDiagnosticsAsync());
        }
    }

    [Fact]
    public async Task SourceGeneratedApiIsIncludedInEveryRule()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "Consumer",
            new[]
            {
                CSharpSyntaxTree.ParseText(
                    """
                    [assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities")]
                    namespace App.Entities;
                    public partial class Entity : Other.GeneratedBase {}
                    """,
                    parseOptions,
                    "Entity.cs")
            },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EntityAccessScopeGenerator().AsSourceGenerator(), new ApiGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        Assert.Empty(generatorDiagnostics);
        Assert.Empty(output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var diagnostics = await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
        Assert.Equal(
            new[] { "EAA0001", "EAA0001", "EAA0002", "EAA0003", "EAA0004" },
            diagnostics.Select(static diagnostic => diagnostic.Id).OrderBy(static id => id));
        Assert.All(diagnostics, diagnostic => Assert.EndsWith("Api.g.cs", Assert.Single(diagnostic.AdditionalLocations).SourceTree?.FilePath));
        Assert.Equal(4, diagnostics.Count(static diagnostic => diagnostic.Location.SourceTree?.FilePath == "Entity.cs"));
        var generatedType = Assert.Single(diagnostics.Where(static diagnostic => diagnostic.Location.SourceTree?.FilePath != "Entity.cs"));
        Assert.Equal("GeneratedEntity", generatedType.Location.SourceTree!.GetText().ToString(generatedType.Location.SourceSpan));
    }

    private sealed class ApiGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(
                static context => context.AddSource(
                    "Api.g.cs",
                    """
                    // <auto-generated/>
                    using System.Collections.Generic;
                    namespace Other
                    {
                        public class GeneratedBase { public void Change() {} }
                    }
                    namespace App.Entities
                    {
                        public partial class Entity
                        {
                            public int Value { get; set; }
                            public List<int> Values => null;
                            public IEnumerable<int> Sequence => null;
                        }
                        public class GeneratedEntity { public int Value; }
                    }
                    """));
        }
    }
}
