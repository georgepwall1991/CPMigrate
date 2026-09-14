namespace CPMigrate.Models;

/// <summary>
/// Represents a property found in a project file.
/// </summary>
/// <param name="Name">The name of the property (element name).</param>
/// <param name="Value">The value of the property.</param>
/// <param name="ProjectPath">The path to the project containing this property.</param>
public record ProjectProperty(string Name, string Value, string ProjectPath);

/// <summary>
/// Represents an item (like Using or PackageReference) found in a project file.
/// </summary>
public record ProjectItem(string ItemType, string Include, string ProjectPath, Dictionary<string, string>? Metadata = null);

/// <summary>
/// Result of analyzing project properties across a solution.
/// </summary>
public class PropertyAnalysisResult
{
    /// <summary>
    /// Map of "PropertyName:Value" key to list of occurrences.
    /// Used to determine frequency of specific Name+Value pairs.
    /// </summary>
    public Dictionary<string, List<ProjectProperty>> PropertyOccurrences { get; set; } = new();

    /// <summary>
    /// Map of "ItemType:Include:MetadataHash" key to list of occurrences.
    /// </summary>
    public Dictionary<string, List<ProjectItem>> ItemOccurrences { get; set; } = new();

    /// <summary>
    /// Items of unify-eligible types that carry a <c>Condition</c> — their own or their group's.
    /// Never counted toward consensus (a conditional item cannot be hoisted unconditionally), but
    /// recorded because such an item stays in its project: unifying the unconditional variant
    /// leaves it seeing both copies.
    /// </summary>
    public List<ProjectItem> ConditionalItems { get; set; } = [];

    /// <summary>
    /// Total number of projects scanned.
    /// </summary>
    public int TotalProjects { get; set; }
}
