using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using MacroSuite.Core.Models;
using MacroSuite.Core.Rules;
using MacroSuite.Core.Steps;

namespace MacroSuite.App.ViewModels;

/// <summary>A virtualized outline of the existing tree. Rows never replace the execution tree.</summary>
public sealed class StructuredActionCollection : IList, INotifyCollectionChanged, INotifyPropertyChanged
{
    public sealed record Entry(MacroStep Step, int RootIndex, Guid? ParentId, string Slot, int ChildIndex, int Depth, string Scope);
    private readonly VirtualizingStepCollection _roots;
    private readonly List<Entry> _rows = new();
    private readonly Dictionary<Guid, MacroStepItemViewModel> _children = new();
    private readonly Queue<Guid> _childOrder = new();
    private readonly HashSet<Guid> _collapsed = new();
    private Guid? _executingId;
    private Guid? _failedId;
    private string _failureMessage = string.Empty;
    public StructuredActionCollection(VirtualizingStepCollection roots) { _roots = roots; Rebuild(); }
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;
    public int Count => _rows.Count;
    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int CachedChildCount => _children.Count;
    public Entry? EntryAt(int index) => index >= 0 && index < _rows.Count ? _rows[index] : null;
    public Entry? Find(Guid id) => _rows.Find(row => row.Step.StepId == id);
    public object? this[int index]
    {
        get
        {
            Entry row = _rows[index];
            MacroStepItemViewModel vm;
            if (row.ParentId is null) vm = _roots[row.RootIndex];
            else if (!_children.TryGetValue(row.Step.StepId, out vm!))
            {
                vm = MacroStepItemViewModel.FromStep(row.Step, row.ChildIndex);
                _children[row.Step.StepId] = vm;
                _childOrder.Enqueue(row.Step.StepId);
                while (_children.Count > VirtualizingStepCollection.DefaultCacheCap)
                    _children.Remove(_childOrder.Dequeue());
            }
            vm.OutlineDepth = row.Depth;
            vm.ScopeLabel = row.Scope;
            vm.IsBlockExpanded = !_collapsed.Contains(row.Step.StepId);
            vm.IsExecuting = row.Step.StepId == _executingId;
            vm.ContainsExecutingAction = !vm.IsExecuting && _executingId.HasValue && ContainsStep(row.Step, _executingId.Value);
            ApplyFailure(vm);
            return vm;
        }
        set => throw new NotSupportedException();
    }
    public void Rebuild()
    {
        _rows.Clear(); _children.Clear(); _childOrder.Clear();
        for (int i = 0; i < _roots.Store.Count; i++) Add(_roots.Store[i], i, null, string.Empty, i, 0, string.Empty);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
    }
    private void Add(MacroStep step, int root, Guid? parent, string slot, int child, int depth, string scope)
    {
        _rows.Add(new Entry(step, root, parent, slot, child, depth, scope));
        if (_collapsed.Contains(step.StepId)) return;
        foreach (var (name, children) in Branches(step))
            for (int i = 0; i < children.Count; i++) Add(children[i], root, step.StepId, name, i, depth + 1, name == "body" ? "Do" : name == "then" ? "Then" : "Else");
    }
    public void Toggle(Guid id) { if (!_collapsed.Add(id)) _collapsed.Remove(id); Rebuild(); }
    public void SetExecution(Guid? id)
    {
        _executingId = id;
        foreach (var vm in _roots.CachedItems.Concat(_children.Values))
        {
            vm.IsExecuting = vm.Step.StepId == id;
            vm.ContainsExecutingAction = !vm.IsExecuting && id.HasValue && ContainsStep(vm.Step, id.Value);
        }
    }
    public void SetFailure(Guid? id, string message)
    {
        _failedId = id; _failureMessage = message;
        foreach (var vm in _roots.CachedItems.Concat(_children.Values)) ApplyFailure(vm);
    }
    private void ApplyFailure(MacroStepItemViewModel vm)
    {
        vm.HasFailure = _failedId.HasValue && ContainsStep(vm.Step, _failedId.Value);
        vm.FailureMessage = vm.HasFailure ? _failureMessage : string.Empty;
    }
    public MacroStepItemViewModel? FindVisibleExecution(Guid id)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].Step.StepId == id || (_collapsed.Contains(_rows[i].Step.StepId) && ContainsStep(_rows[i].Step, id)))
                return (MacroStepItemViewModel?)this[i];
        return null;
    }
    public static IEnumerable<(string Slot, IReadOnlyList<MacroStep> Children)> Branches(MacroStep step)
    {
        if (step is IfThenElseStep iff) { yield return ("then", iff.ThenSteps); yield return ("else", iff.ElseSteps); }
        else if (step is LoopStep loop) yield return ("body", loop.BodySteps);
    }
    public static bool ContainsStep(MacroStep step, Guid id)
        => step.StepId == id || Branches(step).Any(branch => branch.Children.Any(child => ContainsStep(child, id)));
    public int IndexOf(object? value) => value is MacroStepItemViewModel vm ? _rows.FindIndex(r => r.Step.StepId == vm.Step.StepId) : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public IEnumerator GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]!; }
    public void CopyTo(Array array, int index) { for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i); }
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}

public sealed partial class MacroStepItemViewModel
{
    private bool _isExecuting, _containsExecuting, _isBlockExpanded = true;
    private bool _hasFailure;
    private string _failureMessage = string.Empty;
    public int OutlineDepth { get; set; }
    public string ScopeLabel { get; set; } = string.Empty;
    public Thickness OutlineIndent => new(OutlineDepth * 20, 0, 0, 0);
    public bool HasChildren => Step is IfThenElseStep or LoopStep;
    public bool IsBlockExpanded { get => _isBlockExpanded; set { if (SetProperty(ref _isBlockExpanded, value)) OnPropertyChanged(nameof(BlockGlyph)); } }
    public string BlockGlyph => IsBlockExpanded ? "▾" : "▸";
    public bool IsExecuting { get => _isExecuting; set { if (SetProperty(ref _isExecuting, value)) { OnPropertyChanged(nameof(ExecutionLabel)); OnPropertyChanged(nameof(StatusLabel)); } } }
    public bool ContainsExecutingAction { get => _containsExecuting; set { if (SetProperty(ref _containsExecuting, value)) { OnPropertyChanged(nameof(ExecutionLabel)); OnPropertyChanged(nameof(StatusLabel)); } } }
    public bool HasFailure { get => _hasFailure; set { if (SetProperty(ref _hasFailure, value)) OnPropertyChanged(nameof(StatusLabel)); } }
    public string FailureMessage { get => _failureMessage; set => SetProperty(ref _failureMessage, value); }
    public string StatusLabel => HasFailure ? "⚠ Error" : ExecutionLabel;
    public string ExecutionLabel => IsExecuting ? "Executing" : ContainsExecutingAction ? "Running inside" : string.Empty;
    public string ReadableName => Step switch
    {
        ClickStep click when !string.IsNullOrWhiteSpace(click.XVariable) => "Click detected image",
        ClickStep click => $"{click.Button} {(click.Stroke is "Down" or "Up" ? click.Stroke.ToLowerInvariant() : "click")}",
        KeyPressStep key => $"{(key.Stroke == "Down" ? "Hold" : key.Stroke == "Up" ? "Release" : "Press")} {VirtualKeyName(key.KeyCode)}",
        DelayStep => "Wait",
        WaitUntilStep { Condition: ImageCondition } image => image.AppearanceRedirect is not null ? "When image appears" : image.RepeatWhileVisible ? "Repeat while image is visible" : image.WaitWhile ? "Wait for image to disappear" : "Wait for image",
        WaitUntilStep => "Wait for color",
        IfThenElseStep => "If condition matches",
        LoopStep loop => loop.RepeatUntil ? "Repeat until condition matches" : loop.WhileCondition is null ? "Repeat actions" : "While condition matches",
        MouseMoveStep => "Move pointer",
        MouseWheelStep => "Scroll",
        MouseSweepStep => "Camera sweep",
        MouseTrajectoryStep => "Camera path",
        AIStep => "AI vision instruction",
        VisualWaitStep => "Wait for visual condition",
        BreakStep => "Leave loop",
        ContinueStep => "Next iteration",
        _ => Step.Label ?? ActionKeyword
    };
    public string ReadableValues => Step switch
    {
        ClickStep click when !string.IsNullOrWhiteSpace(click.XVariable) => $"Offset {click.OffsetX}, {click.OffsetY}",
        ClickStep click => $"X {click.Position.X}, Y {click.Position.Y}",
        KeyPressStep key => key.Stroke is "Down" or "Up" ? "" : $"Delay after {key.DelayAfterMs} ms",
        WaitUntilStep { Condition: ImageCondition image } wait => $"{(string.IsNullOrWhiteSpace(image.TemplatePath) ? "Saved image" : System.IO.Path.GetFileNameWithoutExtension(image.TemplatePath))} · {image.MinConfidence:P0}" + (wait.RepeatWhileVisible ? $" · Every {wait.RepeatDelayMs} ms" : $" · {wait.Timeout.TotalSeconds:0.#} s timeout"),
        IfThenElseStep iff => $"{iff.ThenSteps.Count} then · {iff.ElseSteps.Count} else",
        LoopStep loop => $"{loop.BodySteps.Count} actions" + (loop.WhileCondition is null ? $" · {loop.MaxIterations} repeats" : ""),
        _ => ParameterTokens
    };
}
