using System.Text.Json.Serialization;

namespace TclRemote;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(SendResponse))]
[JsonSerializable(typeof(KeysResponse))]
[JsonSerializable(typeof(DiscoverResponse))]
[JsonSerializable(typeof(SendRequest))]
[JsonSerializable(typeof(HaOptions))]
[JsonSerializable(typeof(TvDeviceDto))]
[JsonSerializable(typeof(List<TvDeviceDto>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
