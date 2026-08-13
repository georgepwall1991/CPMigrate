using System.Xml.Linq;

namespace CPMigrate.Licensing;

/// <summary>
/// Reads the license fields NuGet writes into a nuspec. Namespaces are ignored because every
/// nuspec schema version uses a different URI for the same local names.
/// </summary>
public static class NuspecLicenseReader
{
    public static bool TryRead(string xml, out NuspecLicense? license)
    {
        license = null;
        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        try
        {
            var document = XDocument.Parse(xml);
            var metadata = Find(document.Root, "metadata") ?? document.Root;
            if (metadata is null)
            {
                return false;
            }

            var licenseElement = Find(metadata, "license");
            if (licenseElement is not null)
            {
                var type = (string?)licenseElement.Attribute("type");
                var value = licenseElement.Value.Trim();
                if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
                {
                    license = new NuspecLicense(value, "file", LicenseUrl: null);
                    return true;
                }

                license = new NuspecLicense(value, "expression", LicenseUrl: null);
                return true;
            }

            var licenseUrl = Find(metadata, "licenseUrl")?.Value.Trim();
            if (!string.IsNullOrEmpty(licenseUrl))
            {
                license = new NuspecLicense(null, "url", licenseUrl);
                return true;
            }

            license = new NuspecLicense(null, "missing", LicenseUrl: null);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryReadFile(string path, out NuspecLicense? license)
    {
        license = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            return TryRead(File.ReadAllText(path), out license);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static XElement? Find(XElement? parent, string localName)
    {
        return parent?.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase)
        );
    }
}
