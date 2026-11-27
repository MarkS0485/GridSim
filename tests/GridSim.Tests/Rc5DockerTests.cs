using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.Licensing;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 docker package: the command line tool gets an image of its own (Dockerfile.cli), and both images look for a
/// licence in a read-only /licence mount through GRIDSIM_LICENCE_FILE, falling back to the free tier when nothing is
/// there. These tests read the Dockerfiles, compose.yml and .dockerignore in the style of BugHunt1101BuildTests; they do
/// NOT build or run an image, so that the images build and start is not shown here. The licence fallback itself is shown
/// on the Core resolution path and through the built CLI.
/// </summary>
public sealed class Rc5DockerTests : IDisposable
{
    private readonly string _dir = TestTemp.Under($"gridsim-rc5-docker-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort: TestTemp sweeps what is left at the end of the run */ }
    }

    // The licence file both images name, built from the names the code reads.
    private const string LicenceEnvLine = "ENV " + LicenceGate.FileEnvVar + "=/licence/licence" + LicenceGate.FileExtension;

    // The runtime-deps image the brief pins (the runtime of SDK 10.0.404 is 10.0.15).
    private const string RuntimeDepsFrom =
        "FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.15@sha256:e6702e45635272155c2aa35568bcbdb975595c0afa73e2656a973d0a87703282 AS runtime";

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), relative)).Replace("\r\n", "\n", StringComparison.Ordinal);

    // Instruction lines only, so a mention in a comment can neither satisfy nor break a check.
    private static string[] Instructions(string text) =>
        text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')).ToArray();

    // The last stage of a Dockerfile: from its FROM line to the end.
    private static string[] RuntimeStage(string dockerfile)
    {
        string[] lines = Instructions(dockerfile);
        int from = Array.FindLastIndex(lines, l => l.StartsWith("FROM ", StringComparison.Ordinal));
        Assert.True(from > 0, "the Dockerfile has no second stage");
        return lines[from..];
    }

    private static string FromLine(string dockerfile, string stage) =>
        Assert.Single(Instructions(dockerfile), l => l.StartsWith("FROM ", StringComparison.Ordinal) && l.EndsWith(" AS " + stage, StringComparison.Ordinal));

    private static string GlobalJsonSdk()
    {
        using JsonDocument doc = JsonDocument.Parse(Read("global.json"));
        return doc.RootElement.GetProperty("sdk").GetProperty("version").GetString()!;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Dockerfile.cli: base images
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Cli_image_builds_on_the_global_json_sdk_pinned_by_digest_the_same_image_as_the_web_build()
    {
        string cli = Read("Dockerfile.cli");
        string sdk = GlobalJsonSdk();
        Assert.Matches($@"^FROM mcr\.microsoft\.com/dotnet/sdk:{Regex.Escape(sdk)}@sha256:[0-9a-f]{{64}} AS build$", FromLine(cli, "build"));
        Assert.Equal(FromLine(Read("Dockerfile"), "build"), FromLine(cli, "build"));
        Assert.Equal(2, Instructions(cli).Count(l => l.StartsWith("FROM ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Cli_image_runs_on_runtime_deps_pinned_by_digest_at_the_web_image_runtime_version()
    {
        string cli = Read("Dockerfile.cli");
        Assert.Equal(RuntimeDepsFrom, FromLine(cli, "runtime"));
        // The web image's ASP.NET Core runtime and the CLI's runtime-deps are the same .NET patch, the one the SDK carries.
        string web = FromLine(Read("Dockerfile"), "runtime");
        string webVersion = Regex.Match(web, @"dotnet/aspnet:(10\.0\.\d+)@").Groups[1].Value;
        string cliVersion = Regex.Match(FromLine(cli, "runtime"), @"dotnet/runtime-deps:(10\.0\.\d+)@").Groups[1].Value;
        Assert.NotEqual("", webVersion);
        Assert.Equal(webVersion, cliVersion);
    }

    [Fact]
    public void Cli_image_publishes_like_the_release_self_contained_so_runtime_deps_is_the_right_base()
    {
        // The release publishes the CLI with the linux-x64 profile (release.yml, job cli); the image does the same, and
        // that profile is self-contained single-file, so the image needs only the native dependencies, not a runtime.
        Assert.Contains("dotnet publish src/GridSim.Cli -c Release -p:PublishProfile=${{ matrix.rid }}", Read(".github/workflows/release.yml"), StringComparison.Ordinal);
        string cli = Read("Dockerfile.cli");
        Assert.Contains("RUN dotnet restore src/GridSim.Cli/GridSim.Cli.csproj --locked-mode -p:PublishProfile=linux-x64", cli, StringComparison.Ordinal);
        Assert.Contains("RUN dotnet publish src/GridSim.Cli/GridSim.Cli.csproj -c Release -p:PublishProfile=linux-x64 ", cli, StringComparison.Ordinal);
        string profile = Read("src/GridSim.Cli/Properties/PublishProfiles/linux-x64.pubxml");
        Assert.Contains("<SelfContained>true</SelfContained>", profile, StringComparison.Ordinal);
        Assert.Contains("<PublishSingleFile>true</PublishSingleFile>", profile, StringComparison.Ordinal);
        Assert.Contains(@"\artifacts\publish\gridsim-linux-x64\</PublishDir>", profile, StringComparison.Ordinal);
        Assert.Contains("COPY --from=build /src/artifacts/publish/gridsim-linux-x64/ ./", RuntimeStage(cli));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Both images: unprivileged user, no package installs, the licence file
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Dockerfile")]
    [InlineData("Dockerfile.cli")]
    public void The_image_runs_as_the_unprivileged_app_user(string dockerfile)
    {
        string[] stage = RuntimeStage(Read(dockerfile));
        int user = Array.IndexOf(stage, "USER $APP_UID");
        Assert.True(user > 0, $"{dockerfile}: the runtime stage has no USER $APP_UID");
        // No later USER line hands the process back to root.
        Assert.DoesNotContain(stage, l => l.StartsWith("USER ", StringComparison.Ordinal) && l != "USER $APP_UID");
        int entry = Array.FindIndex(stage, l => l.StartsWith("ENTRYPOINT ", StringComparison.Ordinal));
        Assert.True(entry > user, $"{dockerfile}: ENTRYPOINT must come after USER $APP_UID");
    }

    [Theory]
    [InlineData("Dockerfile")]
    [InlineData("Dockerfile.cli")]
    public void The_image_installs_no_operating_system_package(string dockerfile)
    {
        var installer = new Regex(@"\b(apt-get|apt|aptitude|apk|yum|dnf|microdnf|tdnf|zypper)\b", RegexOptions.CultureInvariant);
        string[] offending = Instructions(Read(dockerfile)).Where(l => installer.IsMatch(l)).ToArray();
        Assert.True(offending.Length == 0, $"{dockerfile}: " + string.Join(" | ", offending));
    }

    [Fact]
    public void Cli_image_names_the_licence_file_in_the_licence_mount()
    {
        string[] stage = RuntimeStage(Read("Dockerfile.cli"));
        Assert.Contains(LicenceEnvLine, stage);
        Assert.Equal("ENV GRIDSIM_LICENCE_FILE=/licence/licence.gridsim", LicenceEnvLine);
    }

    [Fact]
    public void Both_images_set_the_licence_file_the_same_way_in_their_runtime_stage()
    {
        string[] web = RuntimeStage(Read("Dockerfile")).Where(l => l.Contains(LicenceGate.FileEnvVar, StringComparison.Ordinal)).ToArray();
        string[] cli = RuntimeStage(Read("Dockerfile.cli")).Where(l => l.Contains(LicenceGate.FileEnvVar, StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { LicenceEnvLine }, web);
        Assert.Equal(web, cli);
    }

    [Theory]
    [InlineData("Dockerfile")]
    [InlineData("Dockerfile.cli")]
    public void No_licence_is_built_into_the_image(string dockerfile)
    {
        foreach (string line in Instructions(Read(dockerfile)))
        {
            string l = line.TrimStart();
            // No key in an ENV or ARG (GRIDSIM_LICENCE= is the key variable; the file variable is GRIDSIM_LICENCE_FILE=).
            Assert.DoesNotContain(LicenceGate.EnvVar + "=", l.Replace(LicenceGate.FileEnvVar + "=", "", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.DoesNotContain("Licence__Key", l, StringComparison.Ordinal);
            // No licence file copied in.
            if (l.StartsWith("COPY ", StringComparison.Ordinal) || l.StartsWith("ADD ", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(LicenceGate.FileExtension, l, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("licence", l, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Dockerfile.cli: entry point, working folder, provenance, sources
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Cli_image_runs_the_gridsim_executable_in_work_and_shows_the_help_by_default()
    {
        Assert.Contains("<AssemblyName>gridsim</AssemblyName>", Read("src/GridSim.Cli/GridSim.Cli.csproj"), StringComparison.Ordinal);
        string[] stage = RuntimeStage(Read("Dockerfile.cli"));
        Assert.Equal("ENTRYPOINT [\"gridsim\"]", Assert.Single(stage, l => l.StartsWith("ENTRYPOINT ", StringComparison.Ordinal)));
        Assert.Equal("CMD [\"--help\"]", Assert.Single(stage, l => l.StartsWith("CMD ", StringComparison.Ordinal)));
        Assert.Equal("WORKDIR /work", stage.Last(l => l.StartsWith("WORKDIR ", StringComparison.Ordinal)));
        // The executable is found on the PATH: the published folder is /app.
        Assert.Contains("WORKDIR /app", stage);
        Assert.Contains(stage, l => l.Contains("PATH=\"/app:${PATH}\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Cli_image_takes_the_same_provenance_arguments_as_the_web_image()
    {
        string web = Read("Dockerfile");
        string cli = Read("Dockerfile.cli");
        foreach (string s in new[]
                 {
                     "ARG SOURCE_REVISION=\n", "ARG SBOM_SHA256=\n", "ARG SOURCE_TREE=\n",
                     "-p:SourceRevisionId=\"${SOURCE_REVISION}\"", "-p:GridSimSbomSha256=\"${SBOM_SHA256}\"",
                     "-p:GridSimSourceTree=\"${SOURCE_TREE}\"", " --no-restore\n",
                 })
        {
            Assert.Contains(s, web, StringComparison.Ordinal);
            Assert.Contains(s, cli, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Cli_image_copies_every_project_and_outside_file_the_cli_build_reads()
    {
        string csproj = Read("src/GridSim.Cli/GridSim.Cli.csproj");
        string cli = Read("Dockerfile.cli");
        string[] projects = Regex.Matches(csproj, @"<ProjectReference Include=""\.\.\\(GridSim\.[A-Za-z]+)\\")
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "GridSim.Core", "GridSim.Gda", "GridSim.Gpu" }, projects);
        foreach (string p in projects.Append("GridSim.Cli"))
        {
            Assert.Matches($@"\nCOPY src/{Regex.Escape(p)}/{Regex.Escape(p)}\.csproj\s+src/{Regex.Escape(p)}/packages\.lock\.json\s+src/{Regex.Escape(p)}/\n", cli);
            Assert.Matches($@"\nCOPY src/{Regex.Escape(p)}/\s+src/{Regex.Escape(p)}/\n", cli);
        }
        // Files outside the four projects that the CLI project reads.
        Assert.Contains(@"Include=""..\..\docs\manual\0019 CLI reference.md""", csproj, StringComparison.Ordinal);
        Assert.Contains("COPY [\"docs/manual/0019 CLI reference.md\", \"docs/manual/\"]\n", cli, StringComparison.Ordinal);
        Assert.Contains(@"..\..\tools\crossval\crossval.py;..\..\tools\crossval\sidecar_common.py", csproj, StringComparison.Ordinal);
        Assert.Contains("COPY tools/crossval/crossval.py tools/crossval/sidecar_common.py tools/crossval/\n", cli, StringComparison.Ordinal);
        Assert.Contains(@"<Compile Include=""..\GridSim.Scripting\PathConfinement.cs""", csproj, StringComparison.Ordinal);
        Assert.Contains("COPY src/GridSim.Scripting/PathConfinement.cs src/GridSim.Scripting/\n", cli, StringComparison.Ordinal);
        Assert.Contains(@"<Import Project=""..\DotnetRuntimeNotices.targets"" />", csproj, StringComparison.Ordinal);
        Assert.Contains("COPY src/DotnetRuntimeNotices.targets src/\n", cli, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // compose.yml and .dockerignore
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Compose_mounts_the_licence_folder_read_only_and_passes_no_key_by_default()
    {
        string[] lines = Instructions(Read("compose.yml"));
        Assert.Contains("      - \"${GRIDSIM_LICENCE_DIR:-./licence}:/licence:ro\"", lines);
        Assert.Single(lines, l => l.Contains(":/licence", StringComparison.Ordinal));
        // The key variable is optional and empty unless the operator sets it (an empty key counts as no key).
        Assert.Contains("      Licence__Key: \"${GRIDSIM_LICENCE_KEY:-}\"", lines);
    }

    [Fact]
    public void Dockerignore_keeps_licences_secrets_and_build_outputs_out_of_the_build_context()
    {
        string[] lines = Instructions(Read(".dockerignore")).Select(l => l.Trim()).ToArray();
        foreach (string pattern in new[]
                 {
                     "licence/", "**/*.gridsim", ".env", "**/.env", "compose.override.yml", "**/*.pfx", "**/*.p12",
                     "**/*.pem", "**/*.key", "**/*.snk", "artifacts/", "**/bin/", "**/obj/", ".git",
                 })
            Assert.Contains(pattern, lines);
        // No exception lets a licence or key back in.
        Assert.DoesNotContain(lines, l => l.StartsWith('!') &&
            (l.Contains("licence", StringComparison.OrdinalIgnoreCase) || l.EndsWith(".gridsim", StringComparison.Ordinal) ||
             l.EndsWith(".pfx", StringComparison.Ordinal) || l.EndsWith(".pem", StringComparison.Ordinal) || l.EndsWith(".key", StringComparison.Ordinal)));
    }

    [Fact]
    public void Dockerignore_lets_back_exactly_the_files_the_cli_image_copies_from_ignored_folders()
    {
        string[] lines = Instructions(Read(".dockerignore")).Select(l => l.Trim()).ToArray();
        foreach ((string folder, string file) in new[]
                 {
                     ("docs/", "docs/manual/0019 CLI reference.md"),
                     ("tools/", "tools/crossval/crossval.py"),
                     ("tools/", "tools/crossval/sidecar_common.py"),
                 })
        {
            int exclude = Array.IndexOf(lines, folder);
            int include = Array.IndexOf(lines, "!" + file);
            Assert.True(exclude >= 0, $"{folder} is no longer excluded");
            // The last matching line wins, so the exception must come after the folder's exclusion.
            Assert.True(include > exclude, $"!{file} must follow {folder}");
            Assert.True(File.Exists(Path.Combine(Root(), file)), $"{file} does not exist");
        }
        Assert.Equal(3, lines.Count(l => l.StartsWith('!')));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The licence fallback: a GRIDSIM_LICENCE_FILE that names no file means no licence, never a refusal to start
    // ---------------------------------------------------------------------------------------------------------------

    private static LicenceGate ResolveFromFile(string fileEnvValue)
    {
        using ECDsa verifier = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return LicenceGate.Resolve(null, null, LicenceGate.ReadFileToken(LicenceGate.LicenceFilePathFor(fileEnvValue)), verifier);
    }

    [Fact]
    public void A_licence_file_that_does_not_exist_resolves_to_the_free_tier_with_no_note()
    {
        // As in a container with no /licence mount: neither the file nor its folder exists.
        string missing = Path.Combine(_dir, "licence", "licence.gridsim");
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
        Assert.Null(LicenceGate.ReadFileToken(missing));
        LicenceGate gate = ResolveFromFile(missing);
        Assert.Same(Entitlement.FreeDefault, gate.Entitlement);
        Assert.Null(gate.SourceNote);
        Assert.True(gate.Check(Entitlement.FreeDefault.BusLimit, bundled: false).Allowed);
    }

    [Fact]
    public void A_licence_folder_that_is_mounted_but_empty_resolves_to_the_free_tier_with_no_note()
    {
        Directory.CreateDirectory(_dir);
        string inEmptyFolder = Path.Combine(_dir, "licence.gridsim");
        Assert.Null(LicenceGate.ReadFileToken(inEmptyFolder));
        LicenceGate gate = ResolveFromFile(inEmptyFolder);
        Assert.Same(Entitlement.FreeDefault, gate.Entitlement);
        Assert.Null(gate.SourceNote);
    }

    [Fact]
    public void A_folder_where_the_licence_file_should_be_resolves_to_the_free_tier()
    {
        // docker run -v names a host file that does not exist: Docker creates a folder of that name in its place.
        string folder = Path.Combine(_dir, "licence.gridsim");
        Directory.CreateDirectory(folder);
        Assert.Null(LicenceGate.ReadFileToken(folder));
        LicenceGate gate = ResolveFromFile(folder);
        Assert.Same(Entitlement.FreeDefault, gate.Entitlement);
        Assert.Null(gate.SourceNote);
    }

    [Fact]
    public void An_empty_licence_file_resolves_to_the_free_tier_with_no_note()
    {
        Directory.CreateDirectory(_dir);
        string empty = Path.Combine(_dir, "licence.gridsim");
        File.WriteAllText(empty, "");
        Assert.Equal("", LicenceGate.ReadFileToken(empty));
        LicenceGate gate = ResolveFromFile(empty);
        Assert.Same(Entitlement.FreeDefault, gate.Entitlement);
        Assert.Null(gate.SourceNote);
    }

    [Fact]
    public void A_present_but_corrupt_licence_file_keeps_todays_behaviour_free_tier_with_the_reason_named()
    {
        Directory.CreateDirectory(_dir);
        string corrupt = Path.Combine(_dir, "licence.gridsim");
        File.WriteAllText(corrupt, "# a damaged licence\nGSK1 not-a-real-key\n");
        Assert.Equal("GSK1 not-a-real-key", LicenceGate.ReadFileToken(corrupt));
        LicenceGate gate = ResolveFromFile(corrupt);
        Assert.Same(Entitlement.FreeDefault, gate.Entitlement);
        Assert.NotNull(gate.SourceNote);
        Assert.StartsWith("licence key from licence.gridsim rejected: ", gate.SourceNote, StringComparison.Ordinal);
    }

    [Fact]
    public void A_licence_file_that_cannot_be_written_is_refused_by_name_not_left_as_an_io_crash()
    {
        // In a container /licence is read-only, so installing there must fail as a licence error the CLI reports (exit 2),
        // not as an unhandled I/O exception. A file standing where the folder should be fails the same way on every OS.
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "licence");
        File.WriteAllText(blocker, "a file, not a folder");
        string target = Path.Combine(blocker, "licence.gridsim");

        LicenceException ex = Assert.Throws<LicenceException>(() => LicenceGate.WriteLicenceFile(target, "GSK1 any"));
        Assert.StartsWith($"Could not write the licence file '{target}': ", ex.Message, StringComparison.Ordinal);
        Assert.Equal("a file, not a folder", File.ReadAllText(blocker));

        // The same call into a writable folder writes the key and a line end, as Install always has.
        string ok = Path.Combine(_dir, "ok", "licence.gridsim");
        LicenceGate.WriteLicenceFile(ok, "GSK1 any");
        Assert.Equal("GSK1 any" + Environment.NewLine, File.ReadAllText(ok));
    }

    [Fact]
    public void The_cli_starts_and_solves_at_the_free_tier_when_the_licence_file_does_not_exist()
    {
        string missing = Path.Combine(_dir, "licence", "licence.gridsim");
        var env = new Dictionary<string, string?> { [LicenceGate.FileEnvVar] = missing };

        CliProcess.Result status = CliProcess.RunBuiltWithEnvironment(env, "licence", "status");
        Assert.True(status.ExitCode == 0, status.ToString());
        Assert.Contains($"licence file: {missing}", status.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("rejected", status.StdOut, StringComparison.Ordinal);

        CliProcess.Result pf = CliProcess.RunBuiltWithEnvironment(env, "pf", "case14");
        Assert.True(pf.ExitCode == 0, pf.ToString());
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)), "the CLI must not create the licence folder");
    }
}
