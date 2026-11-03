namespace AuraZip.Core;

public sealed record ShellRequest(string Action, IReadOnlyList<string> Paths)
{
    public static IReadOnlyList<string> Extensions => ArchiveFormats.Extensions;
    public static ShellRequest? Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 4 || arguments[0] != "--shell" || arguments[2] != "--") return null;
        if (arguments[1] is not ("open" or "extract" or "smart" or "here" or "folder" or "test" or "compress")) return null;
        string[] paths;
        try { paths = arguments.Skip(3).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (paths.Length == 0 || (arguments[1] != "compress" && paths.Any(p => !ArchiveFormats.Supported(p)))) return null;
        return new(arguments[1], paths);
    }
}
