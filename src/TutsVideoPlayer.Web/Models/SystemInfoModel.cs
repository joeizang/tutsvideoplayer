using System.Text.Json.Serialization;

namespace TutsVideoPlayer.Web.Models;

public sealed record SystemInfoModel(
    [property: JsonPropertyName("applicationName")] string ApplicationName,
    [property: JsonPropertyName("runtimeDescription")] string RuntimeDescription,
    [property: JsonPropertyName("operatingSystem")] string OperatingSystem,
    [property: JsonPropertyName("fixtureMediaUrl")] string FixtureMediaUrl,
    [property: JsonPropertyName("fixtureSizeBytes")] long FixtureSizeBytes,
    [property: JsonPropertyName("fixtureAvailable")] bool FixtureAvailable);
