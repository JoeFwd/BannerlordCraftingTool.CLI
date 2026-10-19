using System.IO;
using System.Text.Json;

/// The destination the shared --out option names: a created output directory, or stdout when it is absent.
/// Every command asks for the file name it wants and this decides where - or whether - it lands on disk.
sealed class CommandOutput
{
    private CommandOutput(string? directory) => Directory = directory;

    /// The created --out directory, or null when the command prints its JSON to stdout instead.
    public string? Directory { get; }

    public static CommandOutput Read(IReadOnlyDictionary<string, List<string>> options) => new(ReadDirectory(options));

    /// The directory a command that must write files cannot run without.
    public string RequireDirectory() => Directory ?? throw new ArgumentException("--out is required.");

    /// Serialises the report into <out>\fileName, or to stdout when no --out was given.
    public void WriteJson<T>(T value, string fileName)
    {
        var json = JsonSerializer.Serialize(value, CliJson.Options);
        if (Directory is null) Console.WriteLine(json);
        else File.WriteAllText(Path.Combine(Directory, fileName), json);
    }

    private static string? ReadDirectory(IReadOnlyDictionary<string, List<string>> options)
    {
        if (options.ContainsKey("render-dir"))
            throw new ArgumentException("--render-dir is no longer supported. Use --out <dir> instead; --all-pairs writes its PNGs there.");
        if (!options.TryGetValue("out", out var values)) return null;
        if (values.Count != 1) throw new ArgumentException("--out may only be specified once.");
        var directory = Path.GetFullPath(values[0]);
        if (!System.IO.Directory.Exists(directory) && (File.Exists(directory) || NamesAnOutputFile(directory)))
            throw new ArgumentException($"--out must be a directory, but '{values[0]}' looks like a file. Pass a folder; the CLI writes its .json/.png files inside it.");
        System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    /// The file names stale scripts still pass to --out, caught before one silently becomes a directory.
    private static bool NamesAnOutputFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
    }
}
