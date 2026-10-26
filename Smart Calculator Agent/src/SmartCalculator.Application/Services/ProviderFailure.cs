namespace SmartCalculator.Application.Services;

public sealed record ProviderFailure(string Code, string Title, string Detail, int? ProviderStatus);
