using MacroSuite.Core.Models;
using MacroSuite.Core.Steps;

namespace MacroSuite.App.ViewModels;

internal interface IMacroEdit
{
    void Apply(MacroActionStore store);
    void Revert(MacroActionStore store);
}

internal sealed class ReplaceAtEdit : IMacroEdit
{
    public ReplaceAtEdit(int index, MacroStep previous, MacroStep next)
    {
        Index = index;
        Previous = previous;
        Next = next;
    }

    public int Index { get; }
    public MacroStep Previous { get; }
    public MacroStep Next { get; }

    public void Apply(MacroActionStore store) => store.ReplaceAt(Index, Next);
    public void Revert(MacroActionStore store) => store.ReplaceAt(Index, Previous);
}

internal sealed class CompoundEdit(int index, params IMacroEdit[] edits) : IMacroEdit
{
    public int Index { get; } = index;
    public void Apply(MacroActionStore store)
    {
        foreach (var edit in edits) edit.Apply(store);
    }
    public void Revert(MacroActionStore store)
    {
        for (int i = edits.Length - 1; i >= 0; i--) edits[i].Revert(store);
    }
}

internal sealed class InsertEdit : IMacroEdit
{
    public InsertEdit(int index, MacroStep step)
    {
        Index = index;
        Step = step;
    }

    public int Index { get; }
    public MacroStep Step { get; }

    public void Apply(MacroActionStore store) => store.Insert(Index, Step);
    public void Revert(MacroActionStore store) => store.RemoveAt(Index);
}

internal sealed class RemoveEdit : IMacroEdit
{
    public RemoveEdit(int index, MacroStep step)
    {
        Index = index;
        Step = step;
    }

    public int Index { get; }
    public MacroStep Step { get; }

    public void Apply(MacroActionStore store) => store.RemoveAt(Index);
    public void Revert(MacroActionStore store) => store.Insert(Index, Step);
}

internal sealed class MoveEdit : IMacroEdit
{
    public MoveEdit(int from, int to)
    {
        From = from;
        To = to;
    }

    public int From { get; }
    public int To { get; }

    public void Apply(MacroActionStore store) => store.Move(From, To);
    public void Revert(MacroActionStore store) => store.Move(To, From);
}

/// <summary>Bounded undo/redo for editor mutations of <see cref="MacroActionStore"/>.</summary>
internal sealed class EditorHistory
{
    private readonly List<IMacroEdit> _undo = new();
    private readonly List<IMacroEdit> _redo = new();
    private const int MaxDepth = 80;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(IMacroEdit edit)
    {
        if (_undo.Count > 0
            && edit is ReplaceAtEdit next
            && _undo[^1] is ReplaceAtEdit last
            && last.Index == next.Index)
        {
            _undo[^1] = new ReplaceAtEdit(next.Index, last.Previous, next.Next);
            _redo.Clear();
            return;
        }

        _undo.Add(edit);
        if (_undo.Count > MaxDepth)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    public IMacroEdit? PopUndo()
    {
        if (_undo.Count == 0) return null;
        var edit = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(edit);
        return edit;
    }

    public IMacroEdit? PopRedo()
    {
        if (_redo.Count == 0) return null;
        var edit = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(edit);
        return edit;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
