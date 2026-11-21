using System.IO.Compression;
using System.Text.Json;
using Mono.Cecil;

// Checks the built plugin against the assemblies of other Jellyfin versions than the one it was compiled with.
//
//   dotnet run --project tools/AbiCheck -c Release -- <plugin.dll> <version>... [latest]
//
// A plugin is one binary for every Jellyfin release it supports, and Jellyfin changes signatures between releases
// (a return type, a parameter, a new member of an interface). Source that still compiles then fails at run time with
// MissingMethodException or TypeLoadException, on that version only. This tool finds those cases without a server:
// every type, method and field the plugin references in the Jellyfin assemblies must exist there with the same
// signature, and every interface or abstract class of Jellyfin the plugin implements must be implemented in full.
// "latest" stands for the newest stable release on NuGet.

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: AbiCheck <plugin.dll> <jellyfin version>... [latest]");
    return 2;
}

var pluginPath = Path.GetFullPath(args[0]);
string[] packages =
{
    "Jellyfin.Controller", "Jellyfin.Model", "Jellyfin.Common", "Jellyfin.Data", "Jellyfin.Extensions",
    "Jellyfin.Naming", "Jellyfin.Database.Implementations"
};

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
var failed = false;
var checkedVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

foreach (var requested in args.Skip(1))
{
    var version = requested.Equals("latest", StringComparison.OrdinalIgnoreCase) ? await LatestStableAsync() : requested;
    if (!checkedVersions.Add(version))
    {
        continue;
    }

    var problems = Check(await DownloadAsync(version));
    Console.WriteLine($"Jellyfin {version}: {(problems.Count == 0 ? "OK" : problems.Count + " problem(s)")}");
    foreach (var problem in problems)
    {
        Console.WriteLine("  " + problem);
    }

    failed |= problems.Count > 0;
}

return failed ? 1 : 0;

async Task<string> LatestStableAsync()
{
    using var stream = await http.GetStreamAsync("https://api.nuget.org/v3-flatcontainer/jellyfin.controller/index.json");
    using var doc = await JsonDocument.ParseAsync(stream);
    return doc.RootElement.GetProperty("versions").EnumerateArray()
        .Select(v => v.GetString()!)
        .Last(v => !v.Contains('-'));
}

// The lib/ assemblies of the Jellyfin packages of one version, cached in the temp folder.
async Task<string> DownloadAsync(string version)
{
    var dir = Path.Combine(Path.GetTempPath(), "torr2jelly-abi", version);
    Directory.CreateDirectory(dir);

    foreach (var id in packages)
    {
        var marker = Path.Combine(dir, id + ".done");
        if (File.Exists(marker))
        {
            continue;
        }

        var name = id.ToLowerInvariant();
        using var response = await http.GetAsync($"https://api.nuget.org/v3-flatcontainer/{name}/{version}/{name}.{version}.nupkg");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Not every package exists in every version (Jellyfin.Database.Implementations starts with 10.11).
            if (id == "Jellyfin.Controller")
            {
                throw new InvalidOperationException($"Jellyfin {version} was not found on NuGet.");
            }

            File.WriteAllText(marker, "absent");
            continue;
        }

        response.EnsureSuccessStatusCode();
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("lib/", StringComparison.Ordinal) && e.Name.EndsWith(".dll", StringComparison.Ordinal)))
        {
            entry.ExtractToFile(Path.Combine(dir, entry.Name), true);
        }

        File.WriteAllText(marker, "ok");
    }

    return dir;
}

SortedSet<string> Check(string hostDir)
{
    var problems = new SortedSet<string>(StringComparer.Ordinal);
    using var resolver = new DefaultAssemblyResolver();
    resolver.AddSearchDirectory(hostDir);
    var hostAssemblies = Directory.GetFiles(hostDir, "*.dll").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
    using var module = ModuleDefinition.ReadModule(pluginPath, new ReaderParameters { AssemblyResolver = resolver });

    bool IsHost(TypeReference? type)
    {
        while (type is TypeSpecification spec)
        {
            type = spec.ElementType;
        }

        return type?.Scope is AssemblyNameReference name && hostAssemblies.Contains(name.Name);
    }

    TypeDefinition? Resolve(TypeReference type)
    {
        try
        {
            return type.Resolve();
        }
        catch (AssemblyResolutionException)
        {
            return null;
        }
    }

    // 1. Everything the plugin's code refers to.
    foreach (var type in module.GetTypeReferences().Where(IsHost))
    {
        if (Resolve(type) == null)
        {
            problems.Add($"missing type    {type.FullName} [{type.Scope.Name}]");
        }
    }

    foreach (var member in module.GetMemberReferences().Where(m => IsHost(m.DeclaringType)))
    {
        object? resolved = null;
        try
        {
            resolved = member switch
            {
                MethodReference method => method.Resolve(),
                FieldReference field => field.Resolve(),
                _ => member
            };
        }
        catch (AssemblyResolutionException)
        {
        }

        if (resolved == null)
        {
            problems.Add($"missing {(member is MethodReference ? "method " : "field  ")} {member.FullName}");
        }
    }

    // 2. Everything the host expects the plugin's types to implement.
    foreach (var type in module.GetTypes().Where(t => !t.IsInterface))
    {
        foreach (var contract in Contracts(type))
        {
            foreach (var method in contract.Methods.Where(m => m.IsAbstract))
            {
                if (!Implements(type, method))
                {
                    problems.Add($"not implemented {type.FullName} lacks {contract.FullName}::{method.Name}/{method.Parameters.Count}");
                }
            }
        }
    }

    return problems;

    // The host interfaces a plugin type implements (with the interfaces those extend) and its abstract host base classes.
    IEnumerable<TypeDefinition> Contracts(TypeDefinition type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<TypeDefinition>();

        for (var current = type; current != null; current = current.BaseType == null ? null : Resolve(current.BaseType))
        {
            if (current != type && current.Module != module && hostAssemblies.Contains(current.Module.Assembly.Name.Name) && current.IsAbstract && seen.Add(current.FullName))
            {
                yield return current;
            }

            foreach (var implemented in current.Interfaces)
            {
                if (IsHost(implemented.InterfaceType) && Resolve(implemented.InterfaceType) is { } definition)
                {
                    queue.Enqueue(definition);
                }
            }
        }

        while (queue.Count > 0)
        {
            var definition = queue.Dequeue();
            if (!seen.Add(definition.FullName))
            {
                continue;
            }

            yield return definition;
            foreach (var parent in definition.Interfaces)
            {
                if (Resolve(parent.InterfaceType) is { } parentDefinition && hostAssemblies.Contains(parentDefinition.Module.Assembly.Name.Name))
                {
                    queue.Enqueue(parentDefinition);
                }
            }
        }
    }

    // By name and parameter count, anywhere in the type's own hierarchy (an explicit implementation is named "Interface.Member").
    bool Implements(TypeDefinition type, MethodDefinition contractMethod)
    {
        for (var current = type; current != null; current = current.BaseType == null ? null : Resolve(current.BaseType))
        {
            foreach (var candidate in current.Methods)
            {
                if (candidate.IsAbstract || candidate.Parameters.Count != contractMethod.Parameters.Count)
                {
                    continue;
                }

                if (candidate.Name == contractMethod.Name || candidate.Name.EndsWith("." + contractMethod.Name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
