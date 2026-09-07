using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Ashlar.Core.Application.Agent.Models;
using Ashlar.Core.Application.Agent.Ports;
using Ashlar.Core.Domain.Exceptions;
using Ashlar.Abstractions;
using Ashlar.Runtime;
using Ashlar.Core.Application.Maintenance.Ports;
using Ashlar.Policies;
using System.Reflection;

namespace Ashlar.Infrastructure.Agent.Adapters;

/// <summary>
/// Infrastructure adapter for executing agent actions.
/// 
/// Implements IAgentExecutor port from Application layer. Provides:
/// - Agent discovery from service provider
/// - Toolbox setup with available tools
/// - Agent execution with policy enforcement
/// - Error handling and result formatting
/// 
/// Part of the Infrastructure layer, implementing the hexagonal architecture pattern.
/// </summary>
public class AgentExecutorAdapter : IAgentExecutor
{
    private readonly ILogger<AgentExecutorAdapter> _logger;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>Initializes a new agent executor adapter.</summary>
    public AgentExecutorAdapter(
        ILogger<AgentExecutorAdapter> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <summary>Execute asynchronously.</summary>
    public async Task<AgentExecutionResult> ExecuteAsync(
        string agentName,
        FileInfo? inputFile,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;

        _logger.LogInformation(
            "Executing agent: {AgentName}, InputFile: {InputFile}",
            agentName,
            inputFile?.FullName ?? "none");

        try
        {
            // Find and create agent instance
            var agent = FindAgent(agentName);
            if (agent == null)
            {
                throw new AgentExecutionException(
                    agentName,
                    $"Agent '{agentName}' not found",
                    Ashlar.Core.Domain.Exceptions.ErrorCodes.AgentNotFound,
                    $"Use 'ashlar agent list' to see available agents.");
            }

            // Set up toolbox with available tools from DI
            var registry = new CapabilityRegistry();
            
            // Get all registered tools from the service provider
            var tools = _serviceProvider.GetServices<ITool>();
            foreach (var tool in tools)
            {
                try
                {
                    registry.Register(tool);
                    _logger.LogDebug("Registered tool: {ToolId}", tool.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to register tool: {ToolId}", tool.Id);
                }
            }

            // Set up policies
            var policies = new PolicyEngine(new IPolicy[]
            {
                new OutputPathSandboxed(),
                new AllowAllPolicy(),
                new PerfHeadroom(TimeSpan.FromMinutes(5))
            });

            // Create agent host
            var host = new AgentHost(new[] { agent }, registry, policies);

            // Prepare world snapshot with input file if provided
            var snapshotData = new Dictionary<string, object?>
            {
                ["OutputRoot"] = Path.Combine(Directory.GetCurrentDirectory(), "out"),
                ["RepoRoot"] = Directory.GetCurrentDirectory()
            };

            // Provide input path - use provided file or find a default assembly
            if (inputFile != null && inputFile.Exists)
            {
                snapshotData["InputPath"] = inputFile.FullName;
            }
            else
            {
                // Try to find a default assembly in the current directory
                var defaultAssembly = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.dll", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(Directory.GetCurrentDirectory(), "*.exe", SearchOption.TopDirectoryOnly))
                    .FirstOrDefault();

                if (defaultAssembly != null)
                {
                    snapshotData["InputPath"] = defaultAssembly;
                    _logger.LogInformation("Using default assembly: {Path}", defaultAssembly);
                }
                else
                {
                    // If no assembly found, provide empty string but log warning
                    snapshotData["InputPath"] = string.Empty;
                    _logger.LogWarning("No input file provided and no default assembly found. Agent may fail if it requires an input path.");
                }
            }

            var snapshot = new WorldSnapshot(0, snapshotData);

            // Execute agent step
            var delta = await host.StepAsync(snapshot, cancellationToken);

            var duration = DateTime.UtcNow - startTime;

            _logger.LogInformation(
                "Agent execution completed: {AgentName}, Duration: {Duration}ms",
                agentName,
                duration.TotalMilliseconds);

            return new AgentExecutionResult
            {
                AgentName = agentName,
                Success = true,
                Message = $"Agent '{agentName}' executed successfully",
                Duration = duration,
                Output = new { Delta = delta?.Log ?? Array.Empty<string>() }
            };
        }
        catch (AgentExecutionException)
        {
            // Re-throw domain exceptions
            throw;
        }
        catch (TimeoutException ex)
        {
            var duration = DateTime.UtcNow - startTime;
            _logger.LogWarning(ex, "Agent execution timed out: {AgentName}", agentName);
            throw new AgentExecutionException(
                agentName,
                $"Agent execution timed out: {ex.Message}",
                ex);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            _logger.LogError(ex, "Error executing agent: {AgentName}", agentName);
            throw new AgentExecutionException(
                agentName,
                $"Agent execution failed: {ex.Message}",
                ex);
        }
    }

    private IAgent? FindAgent(string agentName)
    {
        try
        {
            // Try to get from service provider first
            try
            {
                var agentType = typeof(IAgent);
                var agents = _serviceProvider.GetServices(agentType).Cast<IAgent>();
                var agent = agents.FirstOrDefault(a => 
                    a.Name.Equals(agentName, StringComparison.OrdinalIgnoreCase));
                
                if (agent != null)
                {
                    return agent;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not find agent in service provider");
            }

            // Try to find in loaded assemblies
            var loadedAgents = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(GetLoadableTypes)
                .Where(t => typeof(IAgent).IsAssignableFrom(t) &&
                           !t.IsInterface &&
                           !t.IsAbstract)
                .Select(t =>
                {
                    try
                    {
                        // Try parameterless constructor first
                        var instance = Activator.CreateInstance(t) as IAgent;
                        if (instance != null && instance.Name.Equals(agentName, StringComparison.OrdinalIgnoreCase))
                        {
                            return instance;
                        }

                        // Try with string parameter (for name)
                        var stringCtor = t.GetConstructor(new[] { typeof(string) });
                        if (stringCtor != null)
                        {
                            instance = stringCtor.Invoke(new object[] { agentName }) as IAgent;
                            if (instance != null)
                            {
                                return instance;
                            }
                        }
                    }
                    catch
                    {
                        // Ignore creation errors
                    }
                    return null;
                })
                .Where(a => a != null)
                .ToList();

            return loadedAgents.FirstOrDefault(a => 
                a!.Name.Equals(agentName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding agent: {AgentName}", agentName);
            return null;
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }
}
