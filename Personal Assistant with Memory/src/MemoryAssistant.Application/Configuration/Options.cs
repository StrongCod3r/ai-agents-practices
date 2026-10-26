using System.ComponentModel.DataAnnotations;

namespace MemoryAssistant.Application.Configuration;

public sealed class AgentOptions
{
    public string Model { get; set; } = "";

    [Range(1, 32)]
    public int MaxRounds { get; set; } = 12;

    [Range(1, 64)]
    public int MaxToolCalls { get; set; } = 24;

    [Range(5, 300)]
    public int TimeoutSeconds { get; set; } = 90;

    [Range(8000, 100000)]
    public int ContextCharacters { get; set; } = 24000;

    [Range(2, 40)]
    public int RecentMessages { get; set; } = 12;

    [Range(4, 200)]
    public int SummaryTriggerMessages { get; set; } = 24;
}

public sealed class EmbeddingOptions
{
    [Required, Url]
    public string BaseUrl { get; set; } = "http://localhost:11435";

    [Required]
    public string Model { get; set; } = "embeddinggemma-2";

    [Range(1, 4096)]
    public int Dimensions { get; set; } = 768;

    [Range(-1.0, 1.0)]
    public double MinSimilarity { get; set; } = .35;

    [Range(1, 90)]
    public int TimeoutSeconds { get; set; } = 60;
}
