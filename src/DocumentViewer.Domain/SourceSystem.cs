using System.Text.Json.Serialization;

namespace DocumentViewer.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceSystem
{
    SalesSystem = 1,
    ServiceSystem = 2
}
