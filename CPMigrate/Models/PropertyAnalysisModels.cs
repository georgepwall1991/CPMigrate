namespace CPMigrate.Models;

/// <summary>
/// Represents a property found in a project file.
/// </summary>
/// <param name="Name">The name of the property (element name).</param>
/// <param name="Value">The value of the property.</param>
/// <param name="ProjectPath">The path to the project containing this property.</param>
public record ProjectProperty(string Name, string Value, string ProjectPath);

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
    /// Total number of projects scanned.
    /// </summary>
    public int TotalProjects { get; set; }
}
