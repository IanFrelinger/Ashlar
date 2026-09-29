using Ashlar.Core.Application.ParallelTesting.Models;
using Ashlar.Core.Application.ParallelTesting.Ports;

namespace Ashlar.Infrastructure.ParallelTesting;

/// <summary>
/// Generates diverse parameter combinations for parallel testing.
/// </summary>
internal sealed class ParameterMatrixGenerator : IParameterMatrixGenerator
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ParameterSet>> GenerateAsync(Scenario scenario, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = new List<ParameterSet>();
        var filters = scenario.Filters.Count > 0 ? scenario.Filters : new[] { "" };
        var categories = scenario.Categories.Count > 0 ? scenario.Categories : new[] { "" };
        foreach (var filter in filters)
        {
            foreach (var category in categories)
            {
                var overrides = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(filter))
                    overrides["filter"] = filter;
                if (!string.IsNullOrEmpty(category))
                    overrides["category"] = category;
                list.Add(new ParameterSet { Overrides = overrides });
            }
        }
        if (list.Count == 0)
            list.Add(new ParameterSet());
        return Task.FromResult<IReadOnlyList<ParameterSet>>(list);
    }
}
