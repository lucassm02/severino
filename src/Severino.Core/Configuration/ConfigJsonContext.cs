using System.Text.Json;
using System.Text.Json.Serialization;

namespace Severino.Core.Configuration;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    RespectNullableAnnotations = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(SeverinoConfig))]
[JsonSerializable(typeof(Routes.RouteFile))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;
