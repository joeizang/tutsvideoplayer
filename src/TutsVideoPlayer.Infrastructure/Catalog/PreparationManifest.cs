using System.Text.Json;
using System.Text.Json.Serialization;

namespace TutsVideoPlayer.Infrastructure.Catalog;

/// <summary>
/// App-owned provenance sidecar beside a permanent playback copy. The manifest
/// is written in a Prepared state before the output is renamed into place, then
/// atomically flipped to Committed; recovery trusts only what it can validate.
/// </summary>
public sealed record PreparationManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("appMarker")]
    public string AppMarker { get; init; } = "tutsvideoplayer";

    [JsonPropertyName("state")]
    public string State { get; init; } = "Prepared";

    [JsonPropertyName("jobId")]
    public long JobId { get; init; }

    [JsonPropertyName("lessonId")]
    public long LessonId { get; init; }

    [JsonPropertyName("sourceGeneration")]
    public int SourceGeneration { get; init; }

    [JsonPropertyName("recipeVersion")]
    public string RecipeVersion { get; init; } = string.Empty;

    [JsonPropertyName("libraryLogicalIdentity")]
    public string? LibraryLogicalIdentity { get; init; }

    [JsonPropertyName("sources")]
    public IReadOnlyList<ManifestSource> Sources { get; init; } = [];

    [JsonPropertyName("outputFileName")]
    public string OutputFileName { get; init; } = string.Empty;

    [JsonPropertyName("outputHash")]
    public string OutputHash { get; init; } = string.Empty;

    [JsonPropertyName("outputLengthBytes")]
    public long OutputLengthBytes { get; init; }

    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    [JsonPropertyName("videoCodec")]
    public string? VideoCodec { get; init; }

    [JsonPropertyName("audioCodec")]
    public string? AudioCodec { get; init; }

    public static PreparationManifest? TryRead(string path)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<PreparationManifest>(File.ReadAllText(path));
            return manifest is null || manifest.AppMarker != "tutsvideoplayer" || manifest.SchemaVersion != 1
                ? null
                : manifest;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    public void WriteAtomically(string path)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}

public sealed record ManifestSource(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("relativePath")] string RelativePath,
    [property: JsonPropertyName("fingerprint")] string Fingerprint);