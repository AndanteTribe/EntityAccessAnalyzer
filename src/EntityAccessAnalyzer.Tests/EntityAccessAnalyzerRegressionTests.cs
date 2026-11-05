using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace EntityAccessAnalyzer.Tests;

public class EntityAccessAnalyzerRegressionTests
{
    [Theory]
    [InlineData("public int {|EAA0001:Value|};", "App.Entities.Entity.Value", "")]
    [InlineData("public static int Value { get; {|EAA0001:set|}; }", "App.Entities.Entity.Value", "")]
    [InlineData("public int this[int i] { get => 0; {|EAA0001:set|} {} }", "App.Entities.Entity.this[int]", "")]
    [InlineData("private int value; public ref int {|EAA0001:Value|} => ref value;", "App.Entities.Entity.Value", "")]
    [InlineData("public void {|EAA0002:Change|}() {}", "App.Entities.Entity.Change()", "")]
    [InlineData("private int value; public ref int {|EAA0001,EAA0002:Get|}() => ref value;", "App.Entities.Entity.Get()", "")]
    [InlineData("public List<int> {|EAA0001,EAA0003:Values|};", "App.Entities.Entity.Values", "System.Collections.Generic.List<int>")]
    [InlineData("public List<int> {|EAA0003:Values|} => null;", "App.Entities.Entity.Values", "System.Collections.Generic.List<int>")]
    [InlineData("public IReadOnlyList<List<int>> {|EAA0003:Values|} => null;", "App.Entities.Entity.Values", "System.Collections.Generic.List<int>")]
    [InlineData("public (List<int>, List<int>) {|EAA0003:Values|} => default;", "App.Entities.Entity.Values", "System.Collections.Generic.List<int>")]
    [InlineData("public IEnumerable<int> {|EAA0004:Values|} => null;", "App.Entities.Entity.Values", "System.Collections.Generic.IEnumerable<int>")]
    [InlineData("public IReadOnlyList<IEnumerable<int>> {|EAA0004:Values|} => null;", "App.Entities.Entity.Values", "System.Collections.Generic.IEnumerable<int>")]
    [InlineData("public void Deconstruct(out List<int> {|EAA0003:values|}) { values = null; }", "App.Entities.Entity.Deconstruct(out System.Collections.Generic.List<int>)", "System.Collections.Generic.List<int>")]
    [InlineData("public void Deconstruct(out IEnumerable<int> {|EAA0004:values|}) { values = null; }", "App.Entities.Entity.Deconstruct(out System.Collections.Generic.IEnumerable<int>)", "System.Collections.Generic.IEnumerable<int>")]
    public async Task DirectDiagnosticsMatchCompletePublicContract(string markedMember, string memberName, string offendingType)
    {
        var markedSource = """
            using System.Collections.Generic;
            [assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities")]
            namespace App.Entities;
            public class Entity
            {
            """ + "\n" + markedMember + "\n}";
        var expected = new List<(string Id, TextSpan Span)>();
        var source = new StringBuilder();
        var previousEnd = 0;
        foreach (Match match in Regex.Matches(markedSource, @"\{\|(?<ids>EAA\d+(?:,EAA\d+)*):(?<text>.*?)\|\}"))
        {
            source.Append(markedSource, previousEnd, match.Index - previousEnd);
            var span = new TextSpan(source.Length, match.Groups["text"].Length);
            foreach (var id in match.Groups["ids"].Value.Split(','))
            {
                expected.Add((id, span));
            }
            source.Append(match.Groups["text"].Value);
            previousEnd = match.Index + match.Length;
        }
        source.Append(markedSource, previousEnd, markedSource.Length - previousEnd);
        Assert.NotEmpty(expected);
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var tree = CSharpSyntaxTree.ParseText(source.ToString(), parseOptions, "Entity.cs");
        var compilation = CSharpCompilation.Create(
            "Consumer",
            new[] { tree },
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
        Assert.Equal(expected.Count, diagnostics.Length);
        foreach (var (id, span) in expected)
        {
            var diagnostic = Assert.Single(diagnostics.Where(diagnostic => diagnostic.Id == id && diagnostic.Location.SourceSpan == span));
            Assert.Same(tree, diagnostic.Location.SourceTree);
            Assert.Equal(tree.GetLineSpan(span), diagnostic.Location.GetLineSpan());
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Empty(diagnostic.AdditionalLocations);
            Assert.Empty(diagnostic.Properties);
            var english = id switch
            {
                "EAA0001" => $"Entity 'App.Entities.Entity' exposes public write access through member '{memberName}'",
                "EAA0002" => $"Entity 'App.Entities.Entity' exposes prohibited public method or operator '{memberName}'",
                "EAA0003" => $"Entity 'App.Entities.Entity' exposes prohibited type '{offendingType}' through member '{memberName}'",
                "EAA0004" => $"Entity 'App.Entities.Entity' exposes enumeration-only type '{offendingType}' through member '{memberName}'; use an allowed collection API",
                _ => throw new InvalidOperationException(id),
            };
            var japanese = id switch
            {
                "EAA0001" => $"Entity 'App.Entities.Entity' はメンバー '{memberName}' を通じてpublicな書き込み操作を公開しています",
                "EAA0002" => $"Entity 'App.Entities.Entity' は禁止されたpublicメソッドまたは演算子 '{memberName}' を公開しています",
                "EAA0003" => $"Entity 'App.Entities.Entity' はメンバー '{memberName}' を通じて禁止された型 '{offendingType}' を公開しています",
                "EAA0004" => $"Entity 'App.Entities.Entity' はメンバー '{memberName}' を通じて列挙専用の型 '{offendingType}' を公開しています。許可されたコレクションAPIを使用してください",
                _ => throw new InvalidOperationException(id),
            };
            Assert.Equal(english, diagnostic.GetMessage(CultureInfo.InvariantCulture));
            Assert.Equal(japanese, diagnostic.GetMessage(CultureInfo.GetCultureInfo("ja-JP")));
        }
    }

    [Theory]
    [InlineData("Api.g.cs", "", null, GeneratedKind.Unknown, true)]
    [InlineData("Api.G.CS", "", null, GeneratedKind.Unknown, true)]
    [InlineData("Api.g.i.cs", "", null, GeneratedKind.Unknown, true)]
    [InlineData("Api.generated.cs", "", null, GeneratedKind.Unknown, true)]
    [InlineData("Api.designer.cs", "", null, GeneratedKind.Unknown, true)]
    [InlineData("TemporaryGeneratedFile_Api.cs", "", null, GeneratedKind.Unknown, true)]
    [InlineData("Api.cs", "// <auto-generated/>\n", null, GeneratedKind.Unknown, true)]
    [InlineData("Api.cs", "/* <autogenerated> */\n", null, GeneratedKind.Unknown, true)]
    [InlineData("Api.cs", "// handwritten code\n", null, GeneratedKind.Unknown, false)]
    [InlineData("Api.cs", "", "true", GeneratedKind.Unknown, true)]
    [InlineData("Api.g.cs", "", "false", GeneratedKind.Unknown, false)]
    [InlineData("Api.g.cs", "", "invalid", GeneratedKind.Unknown, true)]
    [InlineData("Api.cs", "", "invalid", GeneratedKind.Unknown, false)]
    [InlineData("Api.cs", "", null, GeneratedKind.MarkedGenerated, true)]
    [InlineData("Api.g.cs", "", null, GeneratedKind.NotGenerated, false)]
    public async Task GeneratedCodeRecognitionPreservesDiagnosticLocations(
        string path,
        string header,
        string? configured,
        GeneratedKind generatedKind,
        bool generated)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var editable = CSharpSyntaxTree.ParseText(
            """
            [assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities")]
            namespace App.Entities;
            public partial class Entity {}
            """,
            parseOptions,
            "Entity.cs");
        var added = CSharpSyntaxTree.ParseText(
            header + """
            namespace App.Entities;
            public partial class Entity { public int Value { get; set; } }
            """,
            parseOptions,
            path);
        var compilation = CSharpCompilation.Create(
            "Consumer",
            new[] { added, editable },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithSyntaxTreeOptionsProvider(new GeneratedTreeOptions(added, generatedKind)));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EntityAccessScopeGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        Assert.Empty(output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var options = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, new GeneratedConfigOptionsProvider(added, configured));
        var diagnostics = await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()), options)
            .GetAnalyzerDiagnosticsAsync();
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("EAA0001", diagnostic.Id);
        Assert.Equal(
            "Entity 'App.Entities.Entity' exposes public write access through member 'App.Entities.Entity.Value'",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
        if (generated)
        {
            Assert.Same(editable, diagnostic.Location.SourceTree);
            Assert.Equal(new TextSpan(editable.GetText().ToString().IndexOf("Entity {}", StringComparison.Ordinal), 6), diagnostic.Location.SourceSpan);
            var original = Assert.Single(diagnostic.AdditionalLocations);
            Assert.Same(added, original.SourceTree);
            Assert.Equal(new TextSpan(added.GetText().ToString().IndexOf("Value", StringComparison.Ordinal), 5), original.SourceSpan);
        }
        else
        {
            Assert.Same(added, diagnostic.Location.SourceTree);
            Assert.Equal(new TextSpan(added.GetText().ToString().IndexOf("set;", StringComparison.Ordinal), 3), diagnostic.Location.SourceSpan);
            Assert.Empty(diagnostic.AdditionalLocations);
        }
    }

    [Theory]
    [InlineData("[System.CodeDom.Compiler.GeneratedCode(\"tool\", \"1\")]", false)]
    [InlineData("[System.Runtime.CompilerServices.CompilerGenerated]", false)]
    [InlineData("[System.CodeDom.Compiler.GeneratedCode(\"tool\", \"1\")]", true)]
    [InlineData("[System.Runtime.CompilerServices.CompilerGenerated]", true)]
    public async Task GeneratedAttributesIdentifyMemberOrigins(string attribute, bool onType)
    {
        var source = """
            [assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities")]
            namespace App.Entities;
            """ + "\n" + (onType ? attribute : "") + "\npublic class Entity {\n" +
            (onType ? "" : attribute) + "\npublic int Value; }";
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions, "Entity.cs");
        var compilation = CSharpCompilation.Create(
            "Consumer",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EntityAccessScopeGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        Assert.Empty(output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var diagnostic = Assert.Single(
            await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()))
            .GetAnalyzerDiagnosticsAsync());
        Assert.Equal("EAA0001", diagnostic.Id);
        Assert.Same(tree, diagnostic.Location.SourceTree);
        Assert.Equal(new TextSpan(source.IndexOf("Entity {", StringComparison.Ordinal), 6), diagnostic.Location.SourceSpan);
        var original = Assert.Single(diagnostic.AdditionalLocations);
        Assert.Same(tree, original.SourceTree);
        Assert.Equal(new TextSpan(source.IndexOf("Value;", StringComparison.Ordinal), 5), original.SourceSpan);
    }

    [Fact]
    public async Task AnalyzerInstanceIsIsolatedAcrossConcurrentCompilations()
    {
        var analyzer = new EntityAccessAnalyzer();
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path)).ToArray();
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        await Task.WhenAll(
            Enumerable.Range(0, 12).Select(
                async index =>
                {
                    var enabled = index % 2 == 0;
                    var source = "using System.Collections.Generic;\n[assembly: EntityAccessAnalyzer.EntityAccessScope(\"" +
                        (enabled ? "App.Entities" : "Other") + "\")]\nnamespace App.Entities; public class Entity" + index +
                        " { public List<int> Values; }";
                    var compilation = CSharpCompilation.Create(
                        "Consumer" + index,
                        new[] { CSharpSyntaxTree.ParseText(source, parseOptions, "Entity" + index + ".cs") },
                        references,
                        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                    GeneratorDriver driver = CSharpGeneratorDriver.Create(
                        new[] { new EntityAccessScopeGenerator().AsSourceGenerator() },
                        parseOptions: parseOptions);
                    driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
                    Assert.Empty(output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
                    var diagnostics = await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer))
                        .GetAnalyzerDiagnosticsAsync();
                    Assert.Equal(
                        enabled ? new[] { "EAA0001", "EAA0003" } : Array.Empty<string>(),
                        diagnostics.Select(static diagnostic => diagnostic.Id).OrderBy(static id => id));
                    Assert.All(
                        diagnostics,
                        diagnostic => Assert.Contains(
                            "'App.Entities.Entity" + index + ".Values'",
                            diagnostic.GetMessage(CultureInfo.InvariantCulture)));
                }));
    }

    [Theory]
    [InlineData(LanguageVersion.CSharp7_3)]
    [InlineData(LanguageVersion.CSharp8)]
    [InlineData(LanguageVersion.CSharp9)]
    [InlineData(LanguageVersion.CSharp10)]
    [InlineData(LanguageVersion.Preview)]
    public async Task SupportedLanguageVersionsProduceTheSameDiagnostics(LanguageVersion version)
    {
        var source = """
            using System.Collections.Generic;
            [assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities")]
            namespace App.Entities
            {
                public class Entity
                {
                    public int Value;
                    public void Change() {}
                    public List<int> Values => null;
                    public IEnumerable<int> Sequence => null;
                }
            }
            """;
        var parseOptions = new CSharpParseOptions(version);
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
        Assert.Equal(new[] { "EAA0001", "EAA0002", "EAA0003", "EAA0004" }, diagnostics.Select(static diagnostic => diagnostic.Id).OrderBy(static id => id));
    }

    [Fact]
    public void AnalyzerCanBeDiscoveredAndAdvertisesAllDiagnostics()
    {
        var attribute = Assert.Single(
            typeof(EntityAccessAnalyzer).GetCustomAttributes(typeof(DiagnosticAnalyzerAttribute), false)
            .Cast<DiagnosticAnalyzerAttribute>());
        Assert.Equal(new[] { LanguageNames.CSharp }, attribute.Languages);
        var descriptors = new EntityAccessAnalyzer().SupportedDiagnostics;
        Assert.Equal(new[] { "EAA0001", "EAA0002", "EAA0003", "EAA0004" }, descriptors.Select(static descriptor => descriptor.Id));
        Assert.All(
            descriptors,
            descriptor =>
            {
                Assert.Equal("EntityAccessAnalyzer", descriptor.Category);
                Assert.True(descriptor.IsEnabledByDefault);
                Assert.Equal(DiagnosticSeverity.Error, descriptor.DefaultSeverity);
                Assert.Equal("https://github.com/AndanteTribe/EntityAccessAnalyzer/blob/main/docs/diagnostics/" + descriptor.Id + ".md", descriptor.HelpLinkUri);
            });
    }

    [Theory]
    [InlineData("EAA0001")]
    [InlineData("EAA0002")]
    [InlineData("EAA0003")]
    [InlineData("EAA0004")]
    public async Task EachDiagnosticCanBeConfiguredAndSuppressedIndependently(string id)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path)).ToArray();
        foreach (var setting in new[] { ReportDiagnostic.Error, ReportDiagnostic.Warn, ReportDiagnostic.Info, ReportDiagnostic.Hidden, ReportDiagnostic.Suppress })
        {
            var source = """
                using System.Collections.Generic;
                [assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities")]
                namespace App.Entities;
                public class Entity
                {
                    public int Value;
                    public void Change() {}
                    public List<int> Values => null;
                    public IEnumerable<int> Sequence => null;
                }
                """;
            // Exercise pragma suppression separately from compilation-wide configuration.
            foreach (var pragma in new[] { false, true })
            {
                var compilation = CSharpCompilation.Create(
                    "Consumer",
                    new[] { CSharpSyntaxTree.ParseText((pragma ? "#pragma warning disable " + id + "\n" : "") + source, parseOptions) },
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                        .WithSpecificDiagnosticOptions(ImmutableDictionary<string, ReportDiagnostic>.Empty.Add(id, setting)));
                GeneratorDriver driver = CSharpGeneratorDriver.Create(
                    new[] { new EntityAccessScopeGenerator().AsSourceGenerator() },
                    parseOptions: parseOptions);
                driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
                Assert.Empty(output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
                var diagnostics = await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()))
                    .GetAnalyzerDiagnosticsAsync();
                Assert.Equal(pragma || setting == ReportDiagnostic.Suppress ? 3 : 4, diagnostics.Length);
                var unaffected = diagnostics.Where(diagnostic => diagnostic.Id != id).ToArray();
                Assert.Equal(
                    new[] { "EAA0001", "EAA0002", "EAA0003", "EAA0004" }.Where(candidate => candidate != id),
                    unaffected.Select(static diagnostic => diagnostic.Id).OrderBy(static candidate => candidate));
                Assert.All(unaffected, diagnostic => Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity));
                if (pragma || setting == ReportDiagnostic.Suppress)
                {
                    Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == id);
                }
                else
                {
                    var expected = setting switch
                    {
                        ReportDiagnostic.Error => DiagnosticSeverity.Error,
                        ReportDiagnostic.Warn => DiagnosticSeverity.Warning,
                        ReportDiagnostic.Info => DiagnosticSeverity.Info,
                        ReportDiagnostic.Hidden => DiagnosticSeverity.Hidden,
                        _ => throw new InvalidOperationException(),
                    };
                    Assert.Equal(expected, Assert.Single(diagnostics.Where(diagnostic => diagnostic.Id == id)).Severity);
                }
            }
        }
    }

    [Theory]
    [InlineData("System.Collections.Generic", "public interface IReadOnlyList<T> : System.Collections.Generic.ICollection<T> {}", "System.Collections.Generic.IReadOnlyList<int>")]
    [InlineData("System.Collections.Immutable", "public class ImmutableList<T> : System.Collections.Generic.List<T> {}", "System.Collections.Immutable.ImmutableList<int>")]
    public async Task SourceCollectionTypesWithStandardNamesDoNotAcquireStandardExceptions(string namespaceName, string definition, string exposedType)
    {
        var source = "[assembly: EntityAccessAnalyzer.EntityAccessScope(\"App.Entities\")]\nnamespace " + namespaceName + " { " + definition + " }\n" +
            "namespace App.Entities { public class Entity { public " + exposedType + " Values => null; } }";
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
        var diagnostic = Assert.Single(
            await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()))
            .GetAnalyzerDiagnosticsAsync());
        Assert.Equal("EAA0003", diagnostic.Id);
    }

    [Fact]
    public async Task CancellationIsPropagatedWithoutAnalyzerFailureDiagnostics()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "Consumer",
            new[]
            {
                CSharpSyntaxTree.ParseText(
                    """
                    [assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities")]
                    namespace App.Entities;
                    public class Entity { public int Value; }
                    """,
                    parseOptions)
            },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EntityAccessScopeGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var analysis = output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analysis.GetAnalyzerDiagnosticsAsync(cancellation.Token));
        var diagnostic = Assert.Single(await analysis.GetAnalyzerDiagnosticsAsync());
        Assert.Equal("EAA0001", diagnostic.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InheritedDiagnosticsMatchCompleteOrigins(bool generatedBase)
    {
        var baseSource = (generatedBase ? "// <auto-generated/>\n" : "") + """
            using System.Collections.Generic;
            namespace Other;
            public class Base
            {
                public int Value;
                public void Change() {}
                public List<int> Values => null;
                public IEnumerable<int> Sequence => null;
                public virtual int Overridden { get; set; }
            }
            """;
        var entitySource = """
            [assembly: EntityAccessAnalyzer.EntityAccessScope("App.Entities")]
            namespace App.Entities;
            public class Entity : Other.Base
            {
                public override int Overridden { get => 0; }
            }
            """;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path));
        var baseTree = CSharpSyntaxTree.ParseText(baseSource, parseOptions, generatedBase ? "Base.g.cs" : "Base.cs");
        var entityTree = CSharpSyntaxTree.ParseText(entitySource, parseOptions, "Entity.cs");
        var compilation = CSharpCompilation.Create(
            "Consumer",
            new[] { entityTree, baseTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EntityAccessScopeGenerator().AsSourceGenerator() },
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        Assert.Empty(output.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var diagnostics = await output.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new EntityAccessAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
        var expected = new[]
        {
            (Id: "EAA0001", Member: "Value", Original: "Value;", Length: 5,
                Message: "Entity 'App.Entities.Entity' exposes public write access through member 'Other.Base.Value'"),
            (Id: "EAA0002", Member: "Change()", Original: "Change()", Length: 6,
                Message: "Entity 'App.Entities.Entity' exposes prohibited public method or operator 'Other.Base.Change()'"),
            (Id: "EAA0003", Member: "Values", Original: "Values =>", Length: 6,
                Message: "Entity 'App.Entities.Entity' exposes prohibited type 'System.Collections.Generic.List<int>' through member 'Other.Base.Values'"),
            (Id: "EAA0004", Member: "Sequence", Original: "Sequence =>", Length: 8,
                Message: "Entity 'App.Entities.Entity' exposes enumeration-only type 'System.Collections.Generic.IEnumerable<int>' through member 'Other.Base.Sequence'; use an allowed collection API"),
            (Id: "EAA0001", Member: "Overridden", Original: "set;", Length: 3,
                Message: "Entity 'App.Entities.Entity' exposes public write access through member 'Other.Base.Overridden'"),
        };
        Assert.Equal(expected.Length, diagnostics.Length);
        foreach (var item in expected)
        {
            var diagnostic = Assert.Single(
                diagnostics.Where(
                    diagnostic => diagnostic.Id == item.Id &&
                diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("'Other.Base." + item.Member + "'", StringComparison.Ordinal)));
            Assert.Equal(item.Message, diagnostic.GetMessage(CultureInfo.InvariantCulture));
            Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
            Assert.Same(entityTree, diagnostic.Location.SourceTree);
            Assert.Equal(new TextSpan(entitySource.IndexOf("Other.Base", StringComparison.Ordinal), 10), diagnostic.Location.SourceSpan);
            var original = Assert.Single(diagnostic.AdditionalLocations);
            Assert.Same(baseTree, original.SourceTree);
            Assert.Equal(new TextSpan(baseSource.IndexOf(item.Original, StringComparison.Ordinal), item.Length), original.SourceSpan);
        }
    }

    private sealed class GeneratedTreeOptions(SyntaxTree tree, GeneratedKind kind) : SyntaxTreeOptionsProvider
    {
        public override GeneratedKind IsGenerated(SyntaxTree syntaxTree, CancellationToken cancellationToken) =>
            syntaxTree == tree ? kind : GeneratedKind.Unknown;

        public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
        {
            severity = default;
            return false;
        }

        public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity)
        {
            severity = default;
            return false;
        }
    }

    private sealed class GeneratedConfigOptionsProvider(SyntaxTree tree, string? value) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions => new GeneratedConfigOptions(null);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree syntaxTree) => new GeneratedConfigOptions(syntaxTree == tree ? value : null);

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new GeneratedConfigOptions(null);
    }

    private sealed class GeneratedConfigOptions(string? value) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string result)
        {
            result = value ?? "";
            return key == "generated_code" && value != null;
        }
    }
}
