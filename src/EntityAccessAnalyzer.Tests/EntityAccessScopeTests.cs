using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace EntityAccessAnalyzer.Tests;

public class EntityAccessScopeTests
{
    [Fact]
    public void GeneratedAttributeCompilesAndDeclaresAssemblyConfiguration()
    {
        var compilation = Generate(@"
using Target = EntityAccessAnalyzer.EntityAccessScopeAttribute;
[assembly: Target(""*.Entities"", ""*.Entities.*"")]
");
        var configuration = Assert.Single(compilation.Assembly.GetAttributes());
        Assert.Equal(new[] { "*.Entities", "*.Entities.*" },
            configuration.ConstructorArguments.Single().Values.Select(value => (string)value.Value!));
        var attribute = compilation.Assembly.GetTypeByMetadataName(EntityAccessScopeGenerator.AttributeMetadataName);
        Assert.NotNull(attribute);
        Assert.Equal(Accessibility.Internal, attribute!.DeclaredAccessibility);
        Assert.Single(attribute.GetMembers("NamespacePatterns"));
        Assert.True(attribute.InstanceConstructors.Single().Parameters.Single().IsParams);
        var usage = attribute.GetAttributes().Single();
        Assert.Equal(false, usage.NamedArguments.Single(argument => argument.Key == "AllowMultiple").Value.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[assembly: EntityAccessAnalyzer.EntityAccessScope(null)]")]
    [InlineData("[assembly: EntityAccessAnalyzer.EntityAccessScope(\"\")]")]
    [InlineData("[assembly: EntityAccessAnalyzer.EntityAccessScope()]")]
    [InlineData("[assembly: EntityAccessAnalyzer.EntityAccessScope(new string[0])]")]
    [InlineData("[assembly: EntityAccessAnalyzer.EntityAccessScope(new string[] { null, \"\" })]")]
    public void MissingOrEmptyConfigurationCompiles(string attributes)
    {
        var compilation = Generate(attributes);
        if (attributes.Length == 0)
        {
            Assert.Empty(compilation.Assembly.GetAttributes());
        }
        else
        {
            Assert.Single(compilation.Assembly.GetAttributes());
        }
    }

    [Theory]
    [InlineData("\"*.Entities\"", 1)]
    [InlineData("namespacePatterns: new string[] { \"Other.*\", \"*.Entities\" }", 2)]
    [InlineData("null, \"\", \"*.Entities\"", 3)]
    public void SingleExplicitArrayAndMixedPatternsAreSupported(string arguments, int expectedCount)
    {
        var compilation = Generate("[assembly: EntityAccessAnalyzer.EntityAccessScope(" + arguments +
            ")]\nnamespace App.Entities { public class Entity {} }");
        var configuration = Assert.Single(compilation.Assembly.GetAttributes());
        var patterns = configuration.ConstructorArguments.Single().Values;
        Assert.Equal(expectedCount, patterns.Length);
        Assert.Equal("*.Entities", patterns.Last().Value);
    }

    private static Compilation Generate(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp7_3);
        var input = CSharpCompilation.Create("Consumer",
            new[] { CSharpSyntaxTree.ParseText(source, parseOptions) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new EntityAccessScopeGenerator().AsSourceGenerator() }, parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out var output, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.Single(driver.GetRunResult().GeneratedTrees);
        Assert.Empty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        return output;
    }
}
