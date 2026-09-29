using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Bricks;
using Ashlar.Core.Domain.Behaviors;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Clusters;
using Ashlar.Core.Domain.Execution;
using Ashlar.Core.Domain.Execution.Events;

namespace Ashlar.Infrastructure.Execution;

/// <summary>
/// Execution plan for a cluster.
/// </summary>
internal record ExecutionPlan(IReadOnlyList<ExecutionStep> Steps);
