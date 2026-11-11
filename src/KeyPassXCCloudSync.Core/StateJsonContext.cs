using System.Text.Json.Serialization;
namespace KeyPassXCCloudSync;

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
internal partial class StateJsonContext : JsonSerializerContext { }
