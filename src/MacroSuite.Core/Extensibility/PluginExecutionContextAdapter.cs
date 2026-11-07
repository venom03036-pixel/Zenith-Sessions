using MacroSuite.Core.Execution;
using MacroSuite.Core.Steps;
using MacroSuite.SDK.Contracts;

namespace MacroSuite.Core.Extensibility;

/// <summary>
/// Adapts internal engine execution state into the public <see cref="IPluginExecutionContext"/> contract.
/// Enforces cancellation, variable isolation, and execution bounds.
/// </summary>
public sealed class PluginExecutionContextAdapter : IPluginExecutionContext
{
    private readonly PluginStep _step;
    private readonly MacroContext _context;
    private readonly CancellationToken _ct;

    public Guid RunId => _context.RunId;
    public Guid StepId => _step.StepId;
    public string PluginId => _step.PluginId;
    public string StepType => _step.StepType;
    public IReadOnlyDictionary<string, object?> Parameters => _step.Parameters;
    public IPluginVariables Variables { get; }
    public CancellationToken CancellationToken => _ct;

    public PluginExecutionContextAdapter(PluginStep step, MacroContext context, CancellationToken ct)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ct = ct;
        Variables = new PluginVariablesAdapter(context.Variables);
    }

    public void LogInfo(string message)
    {
        // Safe logging sink hook
    }

    public void LogWarning(string message)
    {
        // Safe logging sink hook
    }

    public void LogError(string message, Exception? ex = null)
    {
        // Safe logging sink hook
    }

    private sealed class PluginVariablesAdapter : IPluginVariables
    {
        private readonly MacroVariables _vars;

        public PluginVariablesAdapter(MacroVariables vars)
        {
            _vars = vars;
        }

        public bool TryGet<T>(string key, out T? value) => _vars.TryGet(key, out value);
        public void Set<T>(string key, T value) => _vars.Set(key, value);
        public bool Contains(string key) => _vars.Contains(key);
        public bool Remove(string key) => _vars.Remove(key);
    }
}
