using GridSim.Ui.Models;

namespace GridSim.UiSmoke;

/// <summary>
/// Turns a --case value into the <see cref="ModelSource"/> the window's run pipeline loads, labelled as the app's own
/// model catalogue labels it (so the NETWORK picker and the window title read as in the real app).
/// </summary>
public static class UiSmokeCases
{
    /// <summary>
    /// The repository root (the first folder holding GridSim.slnx) above the working folder, else above the tool's own
    /// folder; null when neither has one (an installed copy run outside a clone).
    /// </summary>
    public static string? FindRepositoryRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (DirectoryInfo? d = new(start); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "GridSim.slnx"))) return d.FullName;
        return null;
    }

    /// <summary>
    /// The source for <paramref name="nameOrPath"/>:
    /// <list type="bullet">
    /// <item><description>an existing *.case.json file: a JSON v2 case (<see cref="ModelCatalog.V2CaseSource"/>);</description></item>
    /// <item><description>an existing .m file: a MATPOWER case, labelled "MATPOWER  &lt;name&gt;";</description></item>
    /// <item><description>an existing folder holding system.json: a v1 case folder (the bundled uk, gb-synthetic and
    /// gb-full folders keep their catalogue labels; any other is "Case  &lt;folder&gt;");</description></item>
    /// <item><description>otherwise a name: data/&lt;name&gt; under the repository when it is a case folder (uk,
    /// gb-synthetic, gb-full), else the one MATPOWER file data/matpower/**/&lt;name&gt;.m.</description></item>
    /// </list>
    /// Throws <see cref="ArgumentException"/> with the reason when nothing matches, or when a name matches more than one
    /// MATPOWER file (it is never guessed).
    /// </summary>
    public static ModelSource Resolve(string nameOrPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrPath);

        // ---- a path the user typed ----
        if (File.Exists(nameOrPath))
        {
            string full = Path.GetFullPath(nameOrPath);
            if (full.EndsWith(".case.json", StringComparison.OrdinalIgnoreCase)) return ModelCatalog.V2CaseSource(full);
            if (full.EndsWith(".m", StringComparison.OrdinalIgnoreCase)) return MatpowerSource(full);
            throw new ArgumentException($"--case {nameOrPath}: a case file must be a MATPOWER .m file or a v2 *.case.json manifest.");
        }
        if (Directory.Exists(nameOrPath))
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(nameOrPath));
            if (!File.Exists(Path.Combine(full, "system.json")))
                throw new ArgumentException($"--case {nameOrPath}: the folder holds no system.json, so it is not a v1 case folder.");
            return FolderSource(full);
        }

        // ---- a name, looked up under the repository's data folder ----
        string root = FindRepositoryRoot()
            ?? throw new ArgumentException($"--case {nameOrPath}: no such file or folder, and no GridSim checkout (GridSim.slnx) was found to look the name up in.");
        string data = Path.Combine(root, "data");
        string folder = Path.Combine(data, nameOrPath);
        if (File.Exists(Path.Combine(folder, "system.json"))) return FolderSource(Path.GetFullPath(folder));

        string matpower = Path.Combine(data, "matpower");
        List<string> hits = Directory.Exists(matpower)
            ? Directory.EnumerateFiles(matpower, nameOrPath + ".m", SearchOption.AllDirectories)
                       .OrderBy(f => f, StringComparer.Ordinal).ToList()
            : new List<string>();
        return hits.Count switch
        {
            1 => MatpowerSource(hits[0]),
            0 => throw new ArgumentException($"--case {nameOrPath}: no case of that name (looked for data/{nameOrPath}/system.json and data/matpower/**/{nameOrPath}.m)."),
            _ => throw new ArgumentException($"--case {nameOrPath}: the name matches {hits.Count} MATPOWER files ({string.Join(", ", hits.Select(h => Path.GetRelativePath(root, h)))}); pass the path of the one you mean."),
        };
    }

    // A MATPOWER source exactly as the catalogue makes it (ModelCatalog.Discover).
    private static ModelSource MatpowerSource(string file) =>
        new($"MATPOWER  {Path.GetFileNameWithoutExtension(file)}", Path.GetDirectoryName(file)!, file);

    // A v1 case folder: the bundled ones keep their catalogue labels (ModelCatalog.Discover), any other is labelled as
    // File > Open case folder... labels it (ModelCatalog.CaseFolderSource).
    private static ModelSource FolderSource(string dir)
    {
        string? parent = Path.GetFileName(Path.GetDirectoryName(dir));
        if (string.Equals(parent, "data", StringComparison.OrdinalIgnoreCase))
        {
            switch (Path.GetFileName(dir))
            {
                case "uk": return new ModelSource("GB reduced  10-zone", dir);
                case "gb-synthetic": return new ModelSource("GB synthetic  distribution depth", dir);
                case "gb-full": return new ModelSource("GB full  3539-bus transmission", dir);
            }
        }
        return ModelCatalog.CaseFolderSource(dir);
    }

    /// <summary>
    /// A short name for the case, used as its sub-folder of the output folder: the --case value's last path part, without
    /// a .m or .case.json ending, with anything that is not a letter, digit, dot, dash or underscore made a dash.
    /// </summary>
    public static string Slug(string nameOrPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrPath);
        string last = Path.GetFileName(Path.TrimEndingDirectorySeparator(nameOrPath.Trim()));
        if (last.EndsWith(".case.json", StringComparison.OrdinalIgnoreCase)) last = last[..^".case.json".Length];
        else if (last.EndsWith(".m", StringComparison.OrdinalIgnoreCase)) last = last[..^2];
        var chars = last.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '-').ToArray();
        string slug = new string(chars).Trim('-', '.');
        return slug.Length == 0 ? "case" : slug;
    }
}
