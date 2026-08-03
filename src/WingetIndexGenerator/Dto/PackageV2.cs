using System.Text.Json.Serialization;

namespace WingetIndexGenerator.Dto;

public class PackageV2
{
    public string? Name { get; set; }
    public string? PackageId { get; set; }
    public string? Version { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Moniker { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IconUrl { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IconSource { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageUrl { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublisherUrl { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Versions { get; set; }
    public IEnumerable<string>? Tags { get; set; }
    public DateTimeOffset? LastUpdate { get; set; }

    public override string ToString()
    {
        return $"{PackageId} [{Version}] {LastUpdate}";
    }
}
