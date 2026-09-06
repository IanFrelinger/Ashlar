namespace Ashlar.Infrastructure.Execution;

// DEPRECATED: IProviderFactory has been moved to Ashlar.Core.Application.Execution.Ports.IProviderFactory
// This file exists temporarily for backward compatibility and will be removed in a future release.
// Update your using statements to: using Ashlar.Core.Application.Execution.Ports;

/// <summary>
/// Factory for creating LLM providers.
/// DEPRECATED: Use Ashlar.Core.Application.Execution.Ports.IProviderFactory instead.
/// </summary>
// TEMP TODO: Migrate callers to Ashlar.Core.Application.Execution.Ports.IProviderFactory then delete this Infrastructure alias
public interface IProviderFactory : Core.Application.Execution.Ports.IProviderFactory
{
}

