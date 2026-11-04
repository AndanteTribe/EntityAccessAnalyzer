using Microsoft.CodeAnalysis;

namespace EntityAccessAnalyzer;

/// <summary>
/// Central registry of the entity API diagnostics.
/// </summary>
internal static class DiagnosticDescriptors
{
    private const string Category = "EntityAccessAnalyzer";
    private const string HelpLinkBase = "https://github.com/AndanteTribe/EntityAccessAnalyzer/blob/main/docs/diagnostics/";

    /// <summary>
    /// EAA0001: an entity exposes a public setter, writable field, or writable reference.
    /// Message arguments: entity, member.
    /// </summary>
    public static readonly DiagnosticDescriptor Eaa0001 = new(
        id: "EAA0001",
        title: new LocalizableResourceString("Eaa0001Title", Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString("Eaa0001Message", Resources.ResourceManager, typeof(Resources)),
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "EAA0001.md");

    /// <summary>
    /// EAA0002: an entity exposes a public method or operator that is not an allowed exception.
    /// Message arguments: entity, member.
    /// </summary>
    public static readonly DiagnosticDescriptor Eaa0002 = new(
        id: "EAA0002",
        title: new LocalizableResourceString("Eaa0002Title", Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString("Eaa0002Message", Resources.ResourceManager, typeof(Resources)),
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "EAA0002.md");

    /// <summary>
    /// EAA0003: an entity exposes a prohibited type, including through a recursively inspected element.
    /// Message arguments: entity, member, prohibited type.
    /// </summary>
    public static readonly DiagnosticDescriptor Eaa0003 = new(
        id: "EAA0003",
        title: new LocalizableResourceString("Eaa0003Title", Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString("Eaa0003Message", Resources.ResourceManager, typeof(Resources)),
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "EAA0003.md");

    /// <summary>
    /// EAA0004: an entity exposes an enumeration-only API, including through a type parameter constraint.
    /// Message arguments: entity, member, enumeration type.
    /// </summary>
    public static readonly DiagnosticDescriptor Eaa0004 = new(
        id: "EAA0004",
        title: new LocalizableResourceString("Eaa0004Title", Resources.ResourceManager, typeof(Resources)),
        messageFormat: new LocalizableResourceString("Eaa0004Message", Resources.ResourceManager, typeof(Resources)),
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpLinkBase + "EAA0004.md");
}
