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

public class BackportedContractTests
{
    [Theory]
    [InlineData("System", "public interface IEquatable<T> { bool Equals(T other); }", "System.IEquatable<Entity>", "public bool Equals(Entity other) => true;", "")]
    [InlineData("System", "public interface IComparable { int CompareTo(object other); }", "System.IComparable", "public int CompareTo(object other) => 0;", "")]
    [InlineData("System", "public interface IComparable<T> { int CompareTo(T other); }", "System.IComparable<Entity>", "public int CompareTo(Entity other) => 0;", "")]
    [InlineData("System.Collections", "public interface IStructuralEquatable { bool Equals(object other, IEqualityComparer comparer); int GetHashCode(IEqualityComparer comparer); }", "System.Collections.IStructuralEquatable", "public bool Equals(object other, System.Collections.IEqualityComparer comparer) => true; public int GetHashCode(System.Collections.IEqualityComparer comparer) => 0;", "")]
    [InlineData("System.Collections", "public interface IStructuralComparable { int CompareTo(object other, IComparer comparer); }", "System.Collections.IStructuralComparable", "public int CompareTo(object other, System.Collections.IComparer comparer) => 0;", "")]
    [InlineData("System.Collections", "public interface IComparer { int Compare(object x, object y); }", "System.Collections.IComparer", "public int Compare(object x, object y) => 0;", "")]
    [InlineData("System.Collections.Generic", "public interface IComparer<T> { int Compare(T x, T y); }", "System.Collections.Generic.IComparer<Entity>", "public int Compare(Entity x, Entity y) => 0;", "")]
    [InlineData("System.Collections", "public interface IEqualityComparer { bool Equals(object x, object y); int GetHashCode(object x); }", "System.Collections.IEqualityComparer", "public new bool Equals(object x, object y) => true; public int GetHashCode(object x) => 0;", "")]
    [InlineData("System.Collections.Generic", "public interface IEqualityComparer<T> { bool Equals(T x, T y); int GetHashCode(T x); }", "System.Collections.Generic.IEqualityComparer<Entity>", "public bool Equals(Entity x, Entity y) => true; public int GetHashCode(Entity x) => 0;", "")]
    [InlineData("System", "public interface IFormattable { string ToString(string format, IFormatProvider provider); }", "System.IFormattable", "public string ToString(string format, IFormatProvider provider) => null;", "")]
    [InlineData("System", "public interface ISpanFormattable : IFormattable { bool TryFormat(Span<char> destination, out int written, ReadOnlySpan<char> format, IFormatProvider provider); }", "System.ISpanFormattable", "public string ToString(string format, IFormatProvider provider) => null; public bool TryFormat(Span<char> destination, out int written, ReadOnlySpan<char> format, IFormatProvider provider) { written = 0; return true; }", "")]
    [InlineData("System", "public interface IUtf8SpanFormattable { bool TryFormat(Span<byte> destination, out int written, ReadOnlySpan<char> format, IFormatProvider provider); }", "System.IUtf8SpanFormattable", "public bool TryFormat(Span<byte> destination, out int written, ReadOnlySpan<char> format, IFormatProvider provider) { written = 0; return true; }", "")]
    [InlineData("System", "public interface IParsable<T> where T : IParsable<T> { static abstract T Parse(string value, IFormatProvider provider); static abstract bool TryParse(string value, IFormatProvider provider, out T result); }", "System.IParsable<Entity>", "public static Entity Parse(string value, IFormatProvider provider) => null; public static bool TryParse(string value, IFormatProvider provider, out Entity result) { result = null; return true; }", "")]
    [InlineData("System", "public interface ISpanParsable<T> where T : ISpanParsable<T> { static abstract T Parse(ReadOnlySpan<char> value, IFormatProvider provider); static abstract bool TryParse(ReadOnlySpan<char> value, IFormatProvider provider, out T result); }", "System.ISpanParsable<Entity>", "public static Entity Parse(ReadOnlySpan<char> value, IFormatProvider provider) => null; public static bool TryParse(ReadOnlySpan<char> value, IFormatProvider provider, out Entity result) { result = null; return true; }", "")]
    [InlineData("System", "public interface IUtf8SpanParsable<T> where T : IUtf8SpanParsable<T> { static abstract T Parse(ReadOnlySpan<byte> value, IFormatProvider provider); static abstract bool TryParse(ReadOnlySpan<byte> value, IFormatProvider provider, out T result); }", "System.IUtf8SpanParsable<Entity>", "public static Entity Parse(ReadOnlySpan<byte> value, IFormatProvider provider) => null; public static bool TryParse(ReadOnlySpan<byte> value, IFormatProvider provider, out Entity result) { result = null; return true; }", "")]
    // The selected policy checks the contract's name, not compatibility with the BCL signature.
    [InlineData("System", "public interface ISpanFormattable { bool TryFormat(int value); }", "System.ISpanFormattable", "public bool TryFormat(int value) => true;", "")]
    [InlineData("System", "public interface ISpanFormattable { ref int TryFormat(); }", "System.ISpanFormattable", "private int value; public ref int TryFormat() => ref value;", "EAA0001")]
    [InlineData("System", "public interface ISpanFormattable { bool TryFormat(int value); }", "System.ISpanFormattable", "public bool TryFormat(int value) => true; public bool TryFormat(string value) => true;", "EAA0002")]
    [InlineData("Other", "public interface ISpanFormattable { bool TryFormat(int value); }", "Other.ISpanFormattable", "public bool TryFormat(int value) => true;", "EAA0002")]
    [InlineData("system", "public interface ISpanFormattable { bool TryFormat(int value); }", "system.ISpanFormattable", "public bool TryFormat(int value) => true;", "EAA0002")]
    [InlineData("System", "public interface ISpanFormattable<T> { bool TryFormat(int value); }", "System.ISpanFormattable<int>", "public bool TryFormat(int value) => true;", "EAA0002")]
    [InlineData("System", "public class Container { public interface ISpanFormattable { bool TryFormat(int value); } }", "System.Container.ISpanFormattable", "public bool TryFormat(int value) => true;", "EAA0002")]
    public async Task SourceContractsAreRecognizedByFullyQualifiedMetadataName(
        string namespaceName,
        string declaration,
        string contractType,
        string implementation,
        string expectedIds)
    {
        var source = "using System;\n[assembly: EntityAccessAnalyzer.EntityAccessScope(\"App.Entities\")]\nnamespace " +
            namespaceName + " { " + declaration + " }\nnamespace App.Entities { public class Entity : " + contractType +
            " { " + implementation + " } }";
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
                Assert.Equal("TryFormat", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
                Assert.Empty(diagnostic.AdditionalLocations);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencedBackportIsRecognizedAlongsideTheFrameworkType(bool inherited)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path)).ToArray();
        var library = CSharpCompilation.Create(
            "Backport",
            new[]
            {
                CSharpSyntaxTree.ParseText(
                    """
                    namespace System
                    {
                        public interface ISpanFormattable : IFormattable
                        {
                            bool TryFormat(Span<char> destination, out int written, ReadOnlySpan<char> format, IFormatProvider provider);
                        }
                    }
                    namespace Other
                    {
                        public class FormattableBase : System.ISpanFormattable
                        {
                            public string ToString(string format, System.IFormatProvider provider) => null;
                            public bool TryFormat(System.Span<char> destination, out int written, System.ReadOnlySpan<char> format, System.IFormatProvider provider)
                            {
                                written = 0;
                                return true;
                            }
                        }
                    }
                    """,
                    parseOptions)
            },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = library.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var implementation = inherited ? "" : """
            public string ToString(string format, System.IFormatProvider provider) => null;
            public bool TryFormat(System.Span<char> destination, out int written, System.ReadOnlySpan<char> format, System.IFormatProvider provider)
            {
                written = 0;
                return true;
            }
            """;
        var source = "extern alias backport;\n[assembly: EntityAccessAnalyzer.EntityAccessScope(\"App.Entities\")]\n" +
            "namespace App.Entities; public class Entity : backport::" + (inherited ? "Other.FormattableBase" : "System.ISpanFormattable") +
            " { " + implementation + " public void Change() {} }";
        var backportReference = MetadataReference.CreateFromImage(stream.ToArray()).WithAliases(ImmutableArray.Create("backport"));
        var compilation = CSharpCompilation.Create(
            "Consumer",
            new[] { CSharpSyntaxTree.ParseText(source, parseOptions) },
            references.Append(backportReference),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EntityAccessScopeGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        Assert.Empty(output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var diagnostic = Assert.Single(
            await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()))
            .GetAnalyzerDiagnosticsAsync());
        Assert.Equal("EAA0002", diagnostic.Id);
        Assert.Equal(
            "Entity 'App.Entities.Entity' exposes prohibited public method or operator 'App.Entities.Entity.Change()'",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }
}
