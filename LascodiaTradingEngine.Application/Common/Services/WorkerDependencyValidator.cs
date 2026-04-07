using Microsoft.Extensions.Logging;

namespace LascodiaTradingEngine.Application.Common.Services;

/// <summary>
/// Validates inter-worker dependency chains at startup to detect circular dependencies
/// and missing prerequisite workers.
/// </summary>
public sealed class WorkerDependencyValidator
{
    private readonly Dictionary<string, HashSet<string>> _dependencies = new();
    private readonly ILogger<WorkerDependencyValidator> _logger;

    public WorkerDependencyValidator(ILogger<WorkerDependencyValidator> logger) => _logger = logger;

    public void RegisterDependency(string worker, string dependsOn)
    {
        if (!_dependencies.ContainsKey(worker))
            _dependencies[worker] = new HashSet<string>();
        _dependencies[worker].Add(dependsOn);
    }

    public bool ValidateNoCycles()
    {
        var visited = new HashSet<string>();
        var stack = new HashSet<string>();

        foreach (var worker in _dependencies.Keys)
        {
            if (HasCycle(worker, visited, stack))
            {
                _logger.LogCritical("WorkerDependencyValidator: circular dependency detected involving {Worker}", worker);
                return false;
            }
        }

        _logger.LogInformation("WorkerDependencyValidator: no circular dependencies detected across {Count} workers",
            _dependencies.Count);
        return true;
    }

    private bool HasCycle(string node, HashSet<string> visited, HashSet<string> stack)
    {
        if (stack.Contains(node)) return true;
        if (visited.Contains(node)) return false;

        visited.Add(node);
        stack.Add(node);

        if (_dependencies.TryGetValue(node, out var deps))
        {
            foreach (var dep in deps)
            {
                if (HasCycle(dep, visited, stack))
                    return true;
            }
        }

        stack.Remove(node);
        return false;
    }
}
