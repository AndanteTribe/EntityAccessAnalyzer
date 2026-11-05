using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace EntityAccessAnalyzer;

/// <summary>
/// Entity API contract checks and source-location lookup.
/// </summary>
internal static class SymbolExtensions
{
    /// <summary>
    /// Reads non-empty namespace patterns from entity-scope attributes defined in the consumer assembly.
    /// Invalid attribute arguments are ignored.
    /// </summary>
    public static IReadOnlyList<string> GetEntityNamespacePatterns(this IAssemblySymbol assembly)
    {
        var scopeAttribute = assembly.GetTypeByMetadataName(EntityAccessScopeGenerator.AttributeMetadataName);
        var patterns = new List<string>();
        foreach (var attribute in assembly.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, scopeAttribute)
                || attribute.ConstructorArguments.Length != 1
                || attribute.ConstructorArguments[0].Kind != TypedConstantKind.Array
                || attribute.ConstructorArguments[0].IsNull)
            {
                continue;
            }
            foreach (var argument in attribute.ConstructorArguments[0].Values)
            {
                if (argument.Value is string { Length: > 0 } pattern)
                {
                    patterns.Add(pattern);
                }
            }
        }
        return patterns;
    }

    /// <summary>
    /// Enumerates public fields, properties and methods from the entity towards its base types, excluding Object.
    /// Tracks derived declarations to omit hidden base members while retaining distinct overloads.
    /// </summary>
    public static IEnumerable<ISymbol> EnumeratePublicApiMembers(
        this INamedTypeSymbol entity,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var visitedBaseTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var derivedMembers = new List<ISymbol>();
        for (var declaringType = entity; declaringType != null && visitedBaseTypes.Add(declaringType); declaringType = declaringType.BaseType)
        {
            if (declaringType.SpecialType == SpecialType.System_Object)
            {
                break;
            }
            foreach (var member in declaringType.GetMembers())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (member.DeclaredAccessibility != Accessibility.Public
                    || member is not (IFieldSymbol or IPropertySymbol or IMethodSymbol))
                {
                    continue;
                }
                if (member is IMethodSymbol
                    {
                        MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or
                        MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or
                        MethodKind.EventRaise or MethodKind.Destructor or MethodKind.ExplicitInterfaceImplementation
                    })
                {
                    continue;
                }

                if (!SymbolEqualityComparer.Default.Equals(declaringType, entity))
                {
                    // LookupSymbols includes hidden overloads. Compare signatures after substituting method type parameters.
                    var hidden = false;
                    foreach (var derived in derivedMembers)
                    {
                        if (derived.Name != member.Name)
                        {
                            continue;
                        }
                        if (member is IMethodSymbol baseMethod && derived is IMethodSymbol derivedMethod)
                        {
                            if (baseMethod.Arity != derivedMethod.Arity)
                            {
                                continue;
                            }
                            var substituted = baseMethod.Arity == 0 ? baseMethod :
                                baseMethod.Construct(derivedMethod.TypeParameters.Cast<ITypeSymbol>().ToArray());
                            var sameSignature = (baseMethod.MethodKind != MethodKind.Conversion
                                    || compilation.ClassifyCommonConversion(substituted.ReturnType, derivedMethod.ReturnType).IsIdentity) &&
                                compilation.HaveSameParameterSignature(substituted.Parameters, derivedMethod.Parameters);
                            if (!sameSignature)
                            {
                                continue;
                            }
                        }
                        else if (member is IPropertySymbol { IsIndexer: true } indexer && derived is IPropertySymbol { IsIndexer: true } derivedIndexer)
                        {
                            if (!compilation.HaveSameParameterSignature(indexer.Parameters, derivedIndexer.Parameters))
                            {
                                continue;
                            }
                        }
                        else if (derived is INamedTypeSymbol { Arity: > 0 })
                        {
                            // A generic nested type does not hide a member referenced without type arguments.
                            continue;
                        }
                        hidden = true;
                        break;
                    }
                    if (hidden)
                    {
                        continue;
                    }
                }
                if (member is IPropertySymbol property)
                {
                    if (!property.ExplicitInterfaceImplementations.IsEmpty)
                    {
                        continue;
                    }
                }
                else if (member is IMethodSymbol method)
                {
                    if (!method.ExplicitInterfaceImplementations.IsEmpty || method.PartialDefinitionPart != null)
                    {
                        continue;
                    }
                }
                yield return member;
            }
            derivedMembers.AddRange(
                declaringType.GetMembers().Where(
                    static member =>
                        member.DeclaredAccessibility == Accessibility.Public
                        && member is not IMethodSymbol
                        {
                            MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or
                            MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise
                        }));
        }
    }

    /// <summary>
    /// Expands transitive type-parameter constraints into named constraints in depth-first order.
    /// Visits each constraint once to handle repeated paths and cycles in incomplete code.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> EnumerateNamedConstraints(
        this ITypeParameterSymbol typeParameter,
        CancellationToken cancellationToken)
    {
        var constraints = new Stack<ITypeSymbol>(typeParameter.ConstraintTypes);
        var visitedConstraints = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        while (constraints.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var constraint = constraints.Pop();
            if (!visitedConstraints.Add(constraint))
            {
                continue;
            }
            if (constraint is ITypeParameterSymbol constrainedParameter)
            {
                foreach (var nestedConstraint in constrainedParameter.ConstraintTypes)
                {
                    constraints.Push(nestedConstraint);
                }
                continue;
            }
            if (constraint is INamedTypeSymbol namedConstraint)
            {
                yield return namedConstraint;
            }
        }
    }

    /// <summary>
    /// Finds the first source location, or returns null for symbols without a source declaration.
    /// </summary>
    public static Location? GetSourceLocation(this ISymbol symbol) =>
        symbol.Locations.FirstOrDefault(static location => location.IsInSource);

    /// <summary>
    /// Checks the ICollection or ICollection&lt;T&gt; definition itself; inherited interfaces are inspected by the caller.
    /// </summary>
    public static bool IsCollectionInterface(this INamedTypeSymbol type, Compilation compilation) =>
        SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, compilation.GetTypeByMetadataName(EntityApiTypeNames.Collection))
        || SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, compilation.GetTypeByMetadataName(EntityApiTypeNames.GenericCollection));

    /// <summary>
    /// Identifies an allowed method contract by fully qualified metadata name, including backported definitions.
    /// </summary>
    public static bool IsAllowedMethodContract(this INamedTypeSymbol type) =>
        type.ContainingType == null
        && EntityApiTypeNames.IsAllowedMethodContract(type.ContainingNamespace.ToDisplayString() + "." + type.MetadataName);

    /// <summary>
    /// Detects field assignment or a writable ref field, including readonly fields that hold a writable reference.
    /// </summary>
    public static bool IsWritable(this IFieldSymbol field) =>
        (!field.IsReadOnly && !field.IsConst) || field.RefKind == RefKind.Ref;

    /// <summary>
    /// Resolves missing accessors through the override chain, retaining the nearest getter and setter independently.
    /// </summary>
    public static (IMethodSymbol? Getter, IMethodSymbol? Setter) GetEffectiveAccessors(this IPropertySymbol property)
    {
        var getter = property.GetMethod;
        var setter = property.SetMethod;
        for (var overridden = property.OverriddenProperty; overridden != null; overridden = overridden.OverriddenProperty)
        {
            getter ??= overridden.GetMethod;
            setter ??= overridden.SetMethod;
        }
        return (getter, setter);
    }

    /// <summary>
    /// Detects a public non-init setter or a public getter returning a writable reference, using effective accessors.
    /// </summary>
    public static bool ExposesWriteAccess(this IPropertySymbol property, IMethodSymbol? getter, IMethodSymbol? setter) =>
        setter is { DeclaredAccessibility: Accessibility.Public, IsInitOnly: false }
        || (getter?.DeclaredAccessibility == Accessibility.Public && property.ReturnsByRef);

    /// <summary>
    /// Recognizes instance Deconstruct methods with a void return and one or more exclusively out parameters.
    /// These parameters are inspected as exposed output types.
    /// </summary>
    public static bool IsDeconstruct(this IMethodSymbol method) =>
        method is { MethodKind: MethodKind.Ordinary, Name: "Deconstruct", IsStatic: false, ReturnsVoid: true, Parameters.IsEmpty: false }
        && method.Parameters.All(static parameter => parameter.RefKind == RefKind.Out);

    /// <summary>
    /// Allows conversion and comparison operators; arithmetic and other user-defined operators remain prohibited.
    /// </summary>
    public static bool IsAllowedOperator(this IMethodSymbol method) =>
        method.MethodKind == MethodKind.Conversion
        || method is
        {
            MethodKind: MethodKind.UserDefinedOperator,
            Name: "op_Equality" or "op_Inequality" or "op_LessThan" or "op_GreaterThan" or "op_LessThanOrEqual" or "op_GreaterThanOrEqual"
        };

    /// <summary>
    /// Identifies the compiler-generated record clone method used by with expressions.
    /// </summary>
    public static bool IsImplicitRecordClone(this IMethodSymbol method) =>
        method.IsImplicitlyDeclared && method.ContainingType.IsRecord && method.Name == "<Clone>$";

    /// <summary>
    /// Collects implementations of allowed interface contracts, including implementations inherited by this entity.
    /// </summary>
    public static HashSet<ISymbol> GetAllowedMethodImplementations(this INamedTypeSymbol entity)
    {
        var implementations = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var contract in entity.AllInterfaces)
        {
            if (!contract.IsAllowedMethodContract())
            {
                continue;
            }
            foreach (var contractMember in contract.GetMembers().OfType<IMethodSymbol>())
            {
                var implementation = entity.FindImplementationForInterfaceMember(contractMember);
                if (implementation != null)
                {
                    implementations.Add(implementation);
                }
            }
        }
        return implementations;
    }

    /// <summary>
    /// Follows overrides to find an allowed interface implementation or the Object Equals/GetHashCode/ToString contract.
    /// Merely hiding an Object method does not qualify.
    /// </summary>
    public static bool IsAllowedMethodImplementation(this IMethodSymbol method, HashSet<ISymbol> allowedMethods)
    {
        for (var overridden = method; overridden != null; overridden = overridden.OverriddenMethod)
        {
            if (allowedMethods.Contains(overridden)
                || (overridden.ContainingType.SpecialType == SpecialType.System_Object && overridden.Name is "Equals" or "GetHashCode" or "ToString"))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Detects constraints deriving from a delegate or an explicitly prohibited mutable type.
    /// Collection-interface constraints are checked separately, accounting for standard collection exceptions.
    /// </summary>
    public static bool HasProhibitedConstraintBaseType(this INamedTypeSymbol constraint, HashSet<INamedTypeSymbol> mutableTypes)
    {
        for (var baseType = constraint; baseType != null; baseType = baseType.BaseType)
        {
            if (baseType.SpecialType is SpecialType.System_Delegate or SpecialType.System_MulticastDelegate
                || mutableTypes.Contains(baseType.OriginalDefinition))
            {
                return true;
            }
        }
        return false;
    }
}