using Asobu.Core;
using Asobu.Core.Accounts;
using Asobu.Core.Minecraft;

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Asobu/0.1 (+https://asobu.cc)");
var meta = new MojangMeta(http);

try
{
    return args switch
    {
        ["versions", ..] => await ListVersionsAsync(meta, args.Contains("--all")),
        ["inspect", var id] => await InspectAsync(meta, id),
        ["install", var id] => await InstallAsync(http, id),
        ["play", var id, ..] => await PlayAsync(http, id, args.Length > 2 ? args[2] : "Player"),
        ["export", var id, var zip] => Export(http, id, zip),
        ["import", var zip] => Import(http, zip),
        ["where"] => Where(),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static int Usage()
{
    Console.WriteLine("""
        asobu — Minecraft launcher (dev harness)

          versions [--all]        list Minecraft versions
          inspect <version>       show what launching a version would require
          install <version>       download everything that version needs
          play <version> [name]   install, then launch it offline
          export <version> <zip>  zip up that version's instance
          import <zip>            import an exported instance under a new id
          where                   print the Asobu data folder
        """);
    return 2;
}

static int Where()
{
    var paths = AsobuPaths.Resolve();
    Console.WriteLine(paths.Root);
    return 0;
}

static async Task<int> ListVersionsAsync(MojangMeta meta, bool all)
{
    var manifest = await meta.GetManifestAsync();

    Console.WriteLine($"latest release   {manifest.Latest.Release}");
    Console.WriteLine($"latest snapshot  {manifest.Latest.Snapshot}");
    Console.WriteLine($"{manifest.Versions.Count} versions total");
    Console.WriteLine();

    var shown = manifest.Versions.Where(v => all || v.IsRelease).ToList();
    foreach (var version in shown.Take(all ? shown.Count : 30))
        Console.WriteLine($"  {version.Id,-22} {version.Type,-10} {version.ReleaseTime:yyyy-MM-dd}");

    if (!all && shown.Count > 30)
        Console.WriteLine($"\n  ... {shown.Count - 30} more. Use --all for every version including snapshots.");

    return 0;
}

static async Task<int> InspectAsync(MojangMeta meta, string id)
{
    var version = await meta.GetResolvedVersionAsync(id);
    var context = RuleContext.Current;

    Console.WriteLine($"{version.Id}  ({version.Type})");
    Console.WriteLine($"  platform      {context.OsName} {context.OsVersion} {context.OsArch}");
    Console.WriteLine($"  main class    {version.MainClass ?? "<missing>"}");
    Console.WriteLine($"  java          {version.JavaVersion?.MajorVersion.ToString() ?? "8 (unspecified)"}"
                    + $"  {version.JavaVersion?.Component}");
    Console.WriteLine($"  asset index   {version.AssetIndex?.Id ?? version.Assets ?? "<missing>"}"
                    + $"  ({Format.Bytes(version.AssetIndex?.TotalSize ?? 0)} of assets)");

    var client = version.ClientJar;
    Console.WriteLine($"  client jar    {Format.Bytes(client?.Size ?? 0)}  sha1 {client?.Sha1 ?? "?"}");
    Console.WriteLine($"  log config    {version.Logging?.Client?.File.Id ?? "none"}");

    var allowed = version.Libraries.Where(l => RuleEvaluator.Allows(l, context)).ToList();
    var natives = allowed.Count(l => l.Natives?.ContainsKey(context.OsName) == true);
    var librarySize = allowed.Sum(l => l.Downloads?.Artifact?.Size ?? 0);

    Console.WriteLine($"  libraries     {allowed.Count} of {version.Libraries.Count} apply here"
                    + $"  ({Format.Bytes(librarySize)}, {natives} with legacy natives)");

    if (version.Arguments is { } arguments)
    {
        var game = arguments.Game.Where(a => RuleEvaluator.Allows(a, context)).SelectMany(a => a.Values).ToList();
        var jvm = arguments.Jvm.Where(a => RuleEvaluator.Allows(a, context)).SelectMany(a => a.Values).ToList();
        Console.WriteLine($"  arg style     structured (1.13+)");
        Console.WriteLine($"  jvm args      {string.Join(' ', jvm)}");
        Console.WriteLine($"  game args     {string.Join(' ', game)}");
    }
    else
    {
        Console.WriteLine($"  arg style     legacy minecraftArguments (1.12.2 and older)");
        Console.WriteLine($"  game args     {version.MinecraftArguments}");
    }

    return 0;
}

static Progress<InstallProgress> Reporter()
{
    var lastStage = "";
    return new Progress<InstallProgress>(p =>
    {
        var line = $"  {p.Stage,-34} {p.Fraction,7:P0}";
        if (p.Stage != lastStage) { Console.WriteLine(); lastStage = p.Stage; }
        Console.Write("\r" + line);
    });
}

static async Task<int> InstallAsync(HttpClient http, string id)
{
    var launcher = new AsobuLauncher(http);
    var instance = FindOrCreate(launcher, id);

    Console.WriteLine($"Installing {id} into {launcher.Paths.Root}");
    await launcher.InstallAsync(instance, Reporter());

    Console.WriteLine("\n\nDone.");
    return 0;
}

static async Task<int> PlayAsync(HttpClient http, string id, string username)
{
    var launcher = new AsobuLauncher(http);
    var instance = FindOrCreate(launcher, id);
    var account = Account.CreateOffline(username);

    Console.WriteLine($"Launching {id} as {username} ({account.Uuid})");

    var process = await launcher.LaunchAsync(
        instance,
        MinecraftSession.ForOffline(account),
        Reporter(),
        onOutput: line => Console.WriteLine("  | " + line));

    Console.WriteLine($"\n\nMinecraft started, pid {process.Id}. Logs in {launcher.Paths.Logs}");
    await process.WaitForExitAsync();
    Console.WriteLine($"Minecraft exited with code {process.ExitCode}.");

    return process.ExitCode;
}

static Asobu.Core.Instances.Instance FindOrCreate(AsobuLauncher launcher, string versionId) =>
    launcher.Instances.LoadAll().FirstOrDefault(i => i.MinecraftVersion == versionId)
    ?? launcher.Instances.Create(versionId, versionId);

static int Export(HttpClient http, string versionId, string zipPath)
{
    var launcher = new AsobuLauncher(http);
    var instance = FindOrCreate(launcher, versionId);
    launcher.Instances.Export(instance, zipPath);
    Console.WriteLine($"Exported '{instance.Name}' to {zipPath}");
    return 0;
}

static int Import(HttpClient http, string zipPath)
{
    var launcher = new AsobuLauncher(http);
    var instance = launcher.Instances.Import(zipPath);
    Console.WriteLine($"Imported '{instance.Name}' as a new instance ({instance.Id})");
    Console.WriteLine($"  version   {instance.MinecraftVersion}");
    Console.WriteLine($"  group     {instance.Group ?? "(none)"}");
    Console.WriteLine($"  icon      {instance.Icon}");
    Console.WriteLine($"  env vars  {instance.EnvironmentVariables.Count}");
    return 0;
}
