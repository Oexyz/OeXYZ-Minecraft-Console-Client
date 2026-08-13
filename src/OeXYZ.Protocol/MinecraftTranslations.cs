using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;

namespace OeXYZ.Protocol;

internal static class MinecraftTranslations
{
    private static readonly Lazy<FrozenDictionary<string, string>> English =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool TryGet(string key, out string pattern) =>
        English.Value.TryGetValue(key, out pattern!);

    private static FrozenDictionary<string, string> Load()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string resource = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("en-us.json", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException("The embedded Minecraft language catalog is missing.");
        Dictionary<string, string>? entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        if (entries is null || entries.Count < 1_000)
            throw new InvalidDataException("The embedded Minecraft language catalog is incomplete.");
        return entries.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
