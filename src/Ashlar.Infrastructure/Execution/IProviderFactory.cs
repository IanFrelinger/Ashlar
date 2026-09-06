namespace Ashlar.Infrastructure.Execution;

// DEPRECATED: IProviderFactory has been moved to Ashlar.Core.Application.Execution.Ports.IProviderFactory
// This file exists temporarily for backward compatibility and will be removed in a future release.
// Update your using statements to: using Ashlar.Core.Application.Execution.Ports;

/// <summary>
/// Factory for creating LLM providers.
/// DEPRECATED: Use Ashlar.Core.Application.Execution.Ports.IProviderFactory instead.
/// </summary>
[Obsolete("IProviderFactory has moved to Ashlar.Core.Application.Execution.Ports. Update your using statements.")]
public interface IProviderFactory : Core.Application.Execution.Ports.IProviderFactory
{
}

