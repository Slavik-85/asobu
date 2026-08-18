using Asobu.Core;
using Asobu.Core.Minecraft;

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Asobu/0.1 (+https://asobu.cc)");
var meta = new MojangMeta(http);

try
{
    return args switch
    {
        ["versions", ..] => await ListVersionsAsync(meta, args.Contains("--all")),
        ["inspect", var id] => await InspectAsync(meta, id),
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

          versions [--all]     list Minecraft versions (releases only unless --all)
          inspect <version>    resolve a version and show what launching it would require
        """);
    return 2;
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
