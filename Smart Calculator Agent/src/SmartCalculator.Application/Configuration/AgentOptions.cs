using System.ComponentModel.DataAnnotations;

namespace SmartCalculator.Application.Configuration;

public sealed class AgentOptions
{
    public const string Section = "Agent";
    [Required] public string Model { get; set; } = "dots-studio/dots-3-note-preview:free";
    [Range(1, 32)] public int MaxRounds { get; set; } = 12;
    [Range(1, 64)] public int MaxToolCalls { get; set; } = 24;
    [Range(5, 300)] public int TimeoutSeconds { get; set; } = 90;
    public bool ReasoningEnabled { get; set; } = true;
}
