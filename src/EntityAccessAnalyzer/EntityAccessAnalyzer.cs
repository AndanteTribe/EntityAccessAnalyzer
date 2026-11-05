using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO.Enumeration;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace EntityAccessAnalyzer;

/// <summary>
/// Restricts the public API of types in the configured entity namespaces.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EntityAccessAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
        DiagnosticDescriptors.Eaa0001,
        DiagnosticDescriptors.Eaa0002,
        DiagnosticDescriptors.Eaa0003,
        DiagnosticDescriptors.Eaa0004);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationStartAction(AnalyzeCompilationStart);
    }

    /// <summary>
    /// Resolves the namespace scope, type policy and generated-code information once per compilation.
    /// Registers semantic-model analysis only when an entity scope is configured.
    /// </summary>
    private static void AnalyzeCompilationStart(CompilationStartAnalysisContext context)
    {
        var patterns = context.Compilation.Assembly.GetEntityNamespacePatterns();
        if (patterns.Count == 0)
        {
            return;
        }

        var allowedCollections = context.Compilation.ResolveAllowedCollections();
        var recursiveTypes = context.Compilation.ResolveAdditionalRecursiveTypes();
        recursiveTypes.UnionWith(allowedCollections);
        var mutableTypes = context.Compilation.ResolveMutableTypes();
        var enumerationTypes = context.Compilation.ResolveEnumerationTypes();

        var generatedCodeAttribute = context.Compilation.GetTypeByMetadataName("System.CodeDom.Compiler.GeneratedCodeAttribute");
        var compilerGeneratedAttribute = context.Compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        var generatedTrees = context.Compilation.GetGeneratedTrees(context.Options.AnalyzerConfigOptionsProvider, context.CancellationToken);

        context.RegisterSemanticModelAction(
            semanticContext => AnalyzeSemanticModel(
                semanticContext,
                patterns,
                allowedCollections,
                recursiveTypes,
                mutableTypes,
                enumerationTypes,
                generatedTrees,
                generatedCodeAttribute,
                compilerGeneratedAttribute));
    }

    /// <summary>
    /// Selects public entities in the configured namespaces and analyzes each partial type at one primary declaration.
    /// </summary>
    private static void AnalyzeSemanticModel(
        SemanticModelAnalysisContext context,
        IReadOnlyList<string> namespacePatterns,
        HashSet<INamedTypeSymbol> allowedCollections,
        HashSet<INamedTypeSymbol> recursiveTypes,
        HashSet<INamedTypeSymbol> mutableTypes,
        HashSet<INamedTypeSymbol> enumerationTypes,
        HashSet<SyntaxTree> generatedTrees,
        INamedTypeSymbol? generatedCodeAttribute,
        INamedTypeSymbol? compilerGeneratedAttribute)
    {
        var model = context.SemanticModel;
        var root = model.SyntaxTree.GetRoot(context.CancellationToken);
        foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (model.GetDeclaredSymbol(declaration, context.CancellationToken) is not { TypeKind: TypeKind.Class or TypeKind.Struct } entity)
            {
                continue;
            }

            var publiclyVisible = true;
            for (var enclosing = entity; enclosing != null; enclosing = enclosing.ContainingType)
            {
                if (enclosing.DeclaredAccessibility != Accessibility.Public)
                {
                    publiclyVisible = false;
                    break;
                }
            }

            var namespaceName = entity.ContainingNamespace.IsGlobalNamespace ? "" : entity.ContainingNamespace.ToDisplayString();
            if (!publiclyVisible
                || !namespacePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern.AsSpan(), namespaceName.AsSpan(), ignoreCase: false)))
            {
                continue;
            }

            // A partial type is analyzed once, preferably in an editable declaration.
            var primaryDeclaration =
                entity.DeclaringSyntaxReferences.FirstOrDefault(reference => !generatedTrees.Contains(reference.SyntaxTree))
                ?? entity.DeclaringSyntaxReferences[0];
            if (primaryDeclaration.SyntaxTree != model.SyntaxTree || primaryDeclaration.Span != declaration.Span)
            {
                continue;
            }

            AnalyzeEntity(
                context,
                declaration,
                entity,
                allowedCollections,
                recursiveTypes,
                mutableTypes,
                enumerationTypes,
                generatedTrees,
                generatedCodeAttribute,
                compilerGeneratedAttribute);
        }
    }

    /// <summary>
    /// Prepares entity/base diagnostic locations and allowed method implementations, then analyzes each visible API member.
    /// </summary>
    private static void AnalyzeEntity(
        SemanticModelAnalysisContext context,
        TypeDeclarationSyntax declaration,
        INamedTypeSymbol entity,
        HashSet<INamedTypeSymbol> allowedCollections,
        HashSet<INamedTypeSymbol> recursiveTypes,
        HashSet<INamedTypeSymbol> mutableTypes,
        HashSet<INamedTypeSymbol> enumerationTypes,
        HashSet<SyntaxTree> generatedTrees,
        INamedTypeSymbol? generatedCodeAttribute,
        INamedTypeSymbol? compilerGeneratedAttribute)
    {
        var model = context.SemanticModel;
        var entityLocation = declaration.Identifier.GetLocation();
        var inheritedLocation = entityLocation;
        if (declaration.BaseList != null)
        {
            foreach (var baseType in declaration.BaseList.Types)
            {
                if (SymbolEqualityComparer.Default.Equals(model.GetTypeInfo(baseType.Type, context.CancellationToken).Type, entity.BaseType))
                {
                    inheritedLocation = baseType.Type.GetLocation();
                    break;
                }
            }
        }

        var allowedMethods = entity.GetAllowedMethodImplementations();

        foreach (var member in entity.EnumeratePublicApiMembers(model.Compilation, context.CancellationToken))
        {
            AnalyzeMember(
                context,
                entity,
                member,
                entityLocation,
                inheritedLocation,
                allowedMethods,
                allowedCollections,
                recursiveTypes,
                mutableTypes,
                enumerationTypes,
                generatedTrees,
                generatedCodeAttribute,
                compilerGeneratedAttribute);
        }
    }

    /// <summary>
    /// Resolves a member's generated/inherited origin, diagnostic locations and effective accessors.
    /// Collects its exposed output types and runs the three diagnostic groups in order.
    /// </summary>
    private static void AnalyzeMember(
        SemanticModelAnalysisContext context,
        INamedTypeSymbol entity,
        ISymbol member,
        Location entityLocation,
        Location inheritedLocation,
        HashSet<ISymbol> allowedMethods,
        HashSet<INamedTypeSymbol> allowedCollections,
        HashSet<INamedTypeSymbol> recursiveTypes,
        HashSet<INamedTypeSymbol> mutableTypes,
        HashSet<INamedTypeSymbol> enumerationTypes,
        HashSet<SyntaxTree> generatedTrees,
        INamedTypeSymbol? generatedCodeAttribute,
        INamedTypeSymbol? compilerGeneratedAttribute)
    {
        var inherited = !SymbolEqualityComparer.Default.Equals(member.ContainingType, entity);
        var sourceLocation = member.GetSourceLocation();
        var generated = sourceLocation?.SourceTree is { } sourceTree && generatedTrees.Contains(sourceTree);
        for (ISymbol? owner = member; owner != null && owner is not INamespaceSymbol; owner = owner.ContainingSymbol)
        {
            if (owner.GetAttributes().Any(
                attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, generatedCodeAttribute)
                    || SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, compilerGeneratedAttribute)))
            {
                generated = true;
                break;
            }
        }

        var location = inherited ? inheritedLocation : generated ? entityLocation : sourceLocation ?? entityLocation;
        var additionalLocations = (inherited || generated) && sourceLocation != null && sourceLocation != location
            ? new[] { sourceLocation } : Array.Empty<Location>();
        var entityName = entity.ToDisplayString();
        var memberName = member.ToDisplayString();
        IMethodSymbol? getter = null;
        IMethodSymbol? setter = null;
        var deconstruct = false;

        if (member is IPropertySymbol property)
        {
            (getter, setter) = property.GetEffectiveAccessors();
        }
        else if (member is IMethodSymbol method)
        {
            deconstruct = method.IsDeconstruct();
        }

        var outputs = CollectOutputTypes(member, getter, deconstruct, inherited || generated, location);
        AnalyzeWriteAccess(
            context,
            entity,
            member,
            getter,
            setter,
            generatedTrees,
            inherited,
            generated,
            location,
            inheritedLocation,
            additionalLocations);
        if (member is IMethodSymbol methodSymbol)
        {
            AnalyzeMethod(
                context,
                methodSymbol,
                deconstruct,
                allowedMethods,
                location,
                additionalLocations,
                entityName,
                memberName);
        }

        AnalyzeOutputTypes(
            context,
            outputs,
            allowedCollections,
            recursiveTypes,
            mutableTypes,
            enumerationTypes,
            additionalLocations,
            entityName,
            memberName);
    }

    /// <summary>
    /// Collects field and readable-property types, conversion results and Deconstruct outputs with their diagnostic locations.
    /// Pushes Deconstruct parameters in reverse so stack traversal inspects them in declaration order.
    /// </summary>
    private static Stack<(ITypeSymbol Type, Location Location)> CollectOutputTypes(
        ISymbol member,
        IMethodSymbol? getter,
        bool deconstruct,
        bool useMemberLocation,
        Location location)
    {
        var outputs = new Stack<(ITypeSymbol Type, Location Location)>();
        if (member is IFieldSymbol field)
        {
            outputs.Push((field.Type, location));
        }
        else if (member is IPropertySymbol property)
        {
            if (getter?.DeclaredAccessibility == Accessibility.Public)
            {
                outputs.Push((property.Type, location));
            }
        }
        else if (member is IMethodSymbol method)
        {
            if (method.MethodKind == MethodKind.Conversion)
            {
                outputs.Push((method.ReturnType, location));
            }
            else if (deconstruct)
            {
                foreach (var parameter in method.Parameters.Reverse())
                {
                    outputs.Push((parameter.Type, useMemberLocation ? location : parameter.GetSourceLocation() ?? location));
                }
            }
        }
        return outputs;
    }

    /// <summary>
    /// Reports EAA0001 for writable fields, properties and reference returns.
    /// </summary>
    private static void AnalyzeWriteAccess(
        SemanticModelAnalysisContext context,
        INamedTypeSymbol entity,
        ISymbol member,
        IMethodSymbol? getter,
        IMethodSymbol? setter,
        HashSet<SyntaxTree> generatedTrees,
        bool inherited,
        bool generated,
        Location location,
        Location inheritedLocation,
        IReadOnlyList<Location> additionalLocations)
    {
        if (member is IPropertySymbol property)
        {
            AnalyzePropertyWriteAccess(
                context,
                entity,
                property,
                getter,
                setter,
                generatedTrees,
                inherited,
                generated,
                location,
                inheritedLocation,
                additionalLocations);
        }
        else if ((member is IFieldSymbol field && field.IsWritable()) || member is IMethodSymbol { ReturnsByRef: true })
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    DiagnosticDescriptors.Eaa0001,
                    location,
                    additionalLocations,
                    null,
                    entity.ToDisplayString(),
                    member.ToDisplayString()));
        }
    }

    /// <summary>
    /// Reports property write access at the editable setter when possible, or at the entity/base declaration
    /// for generated or inherited accessors, preserving the original accessor as an additional location.
    /// </summary>
    private static void AnalyzePropertyWriteAccess(
        SemanticModelAnalysisContext context,
        INamedTypeSymbol entity,
        IPropertySymbol property,
        IMethodSymbol? getter,
        IMethodSymbol? setter,
        HashSet<SyntaxTree> generatedTrees,
        bool inherited,
        bool generated,
        Location location,
        Location inheritedLocation,
        IReadOnlyList<Location> additionalLocations)
    {
        if (!property.ExposesWriteAccess(getter, setter))
        {
            return;
        }

        var writeLocation = location;
        var writeAdditionalLocations = additionalLocations;
        var writeMemberName = property.ToDisplayString();
        if (setter is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false } &&
            !SymbolEqualityComparer.Default.Equals(setter.ContainingType, property.ContainingType))
        {
            writeLocation = inheritedLocation;
            writeMemberName = setter.AssociatedSymbol?.ToDisplayString() ?? setter.ToDisplayString();
            var setterLocation = setter.GetSourceLocation();
            if (setterLocation != null)
            {
                writeAdditionalLocations = new[] { setterLocation };
            }
        }
        else if (!inherited && !generated && setter?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken)
            is AccessorDeclarationSyntax accessor && !generatedTrees.Contains(accessor.SyntaxTree) &&
            SymbolEqualityComparer.Default.Equals(setter.ContainingType, entity))
        {
            writeLocation = accessor.Keyword.GetLocation();
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                DiagnosticDescriptors.Eaa0001,
                writeLocation,
                writeAdditionalLocations,
                null,
                entity.ToDisplayString(),
                writeMemberName));
    }

    /// <summary>
    /// Reports EAA0002 for methods outside the allowed contracts and language-defined exceptions.
    /// </summary>
    private static void AnalyzeMethod(
        SemanticModelAnalysisContext context,
        IMethodSymbol method,
        bool deconstruct,
        HashSet<ISymbol> allowedMethods,
        Location location,
        IReadOnlyList<Location> additionalLocations,
        string entityName,
        string memberName)
    {
        if (!deconstruct && !method.IsAllowedOperator() && !method.IsImplicitRecordClone() &&
            !method.IsAllowedMethodImplementation(allowedMethods))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    DiagnosticDescriptors.Eaa0002,
                    location,
                    additionalLocations,
                    null,
                    entityName,
                    memberName));
        }
    }

    /// <summary>
    /// Reports EAA0003 and EAA0004 while recursively inspecting exposed types and constraints.
    /// </summary>
    private static void AnalyzeOutputTypes(
        SemanticModelAnalysisContext context,
        Stack<(ITypeSymbol Type, Location Location)> outputs,
        HashSet<INamedTypeSymbol> allowedCollections,
        HashSet<INamedTypeSymbol> recursiveTypes,
        HashSet<INamedTypeSymbol> mutableTypes,
        HashSet<INamedTypeSymbol> enumerationTypes,
        IReadOnlyList<Location> additionalLocations,
        string entityName,
        string memberName)
    {
        // One visit per exposed type also deduplicates repeated tuple elements and out parameters.
        var inspected = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        while (outputs.Count > 0)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var (type, outputLocation) = outputs.Pop();
            if (!inspected.Add(type) || type.TypeKind is TypeKind.Error or TypeKind.Pointer or TypeKind.FunctionPointer)
            {
                continue;
            }

            var prohibited = type is IArrayTypeSymbol || type.TypeKind == TypeKind.Delegate;
            var enumerationOnly = false;
            if (type is ITypeParameterSymbol typeParameter)
            {
                (prohibited, enumerationOnly) = InspectTypeParameterConstraints(
                    context,
                    typeParameter,
                    outputs,
                    outputLocation,
                    allowedCollections,
                    recursiveTypes,
                    mutableTypes,
                    enumerationTypes);
            }
            else if (type is INamedTypeSymbol namedType)
            {
                (prohibited, enumerationOnly) = InspectNamedOutputType(
                    context.SemanticModel.Compilation,
                    namedType,
                    outputs,
                    outputLocation,
                    recursiveTypes,
                    mutableTypes,
                    enumerationTypes);
            }

            if (prohibited)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.Eaa0003,
                        outputLocation,
                        additionalLocations,
                        null,
                        entityName,
                        memberName,
                        type.ToDisplayString()));
            }
            if (enumerationOnly)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        DiagnosticDescriptors.Eaa0004,
                        outputLocation,
                        additionalLocations,
                        null,
                        entityName,
                        memberName,
                        type.ToDisplayString()));
            }
        }
    }

    /// <summary>
    /// Inspects transitive generic constraints and queues type arguments from recursive constraints.
    /// Combines all constraints before deciding EAA0004: prohibited types or guaranteed allowed collections suppress it.
    /// </summary>
    private static (bool Prohibited, bool EnumerationOnly) InspectTypeParameterConstraints(
        SemanticModelAnalysisContext context,
        ITypeParameterSymbol typeParameter,
        Stack<(ITypeSymbol Type, Location Location)> outputs,
        Location outputLocation,
        HashSet<INamedTypeSymbol> allowedCollections,
        HashSet<INamedTypeSymbol> recursiveTypes,
        HashSet<INamedTypeSymbol> mutableTypes,
        HashSet<INamedTypeSymbol> enumerationTypes)
    {
        var compilation = context.SemanticModel.Compilation;
        var prohibited = false;
        var enumerationOnly = false;
        var guaranteesCollection = false;
        foreach (var namedConstraint in typeParameter.EnumerateNamedConstraints(context.CancellationToken))
        {
            if (recursiveTypes.Contains(namedConstraint.OriginalDefinition))
            {
                foreach (var argument in namedConstraint.TypeArguments)
                {
                    outputs.Push((argument, outputLocation));
                }
            }
            prohibited |= namedConstraint.HasProhibitedConstraintBaseType(mutableTypes);
            foreach (var contract in namedConstraint.AllInterfaces.Prepend(namedConstraint))
            {
                var definition = contract.OriginalDefinition;
                prohibited |= !allowedCollections.Contains(namedConstraint.OriginalDefinition) &&
                    contract.IsCollectionInterface(compilation);
                guaranteesCollection |= allowedCollections.Contains(definition);
                enumerationOnly |= enumerationTypes.Contains(definition);
            }
        }
        enumerationOnly &= !prohibited && !guaranteesCollection;
        return (prohibited, enumerationOnly);
    }

    /// <summary>
    /// Schedules arguments of approved recursive types for declaration-order inspection; otherwise classifies mutable collections,
    /// delegates and enumeration-only interfaces without descending into arbitrary generic types.
    /// </summary>
    private static (bool Prohibited, bool EnumerationOnly) InspectNamedOutputType(
        Compilation compilation,
        INamedTypeSymbol type,
        Stack<(ITypeSymbol Type, Location Location)> outputs,
        Location outputLocation,
        HashSet<INamedTypeSymbol> recursiveTypes,
        HashSet<INamedTypeSymbol> mutableTypes,
        HashSet<INamedTypeSymbol> enumerationTypes)
    {
        var prohibited = type.TypeKind == TypeKind.Delegate;
        var enumerationOnly = false;
        var definition = type.OriginalDefinition;
        if (recursiveTypes.Contains(definition))
        {
            foreach (var argument in type.TypeArguments.Reverse())
            {
                outputs.Push((argument, outputLocation));
            }
        }
        else
        {
            prohibited |= mutableTypes.Contains(definition) || type.IsCollectionInterface(compilation)
                || type.AllInterfaces.Any(contract => contract.IsCollectionInterface(compilation));
            enumerationOnly = enumerationTypes.Contains(definition);
        }
        return (prohibited, enumerationOnly);
    }
}