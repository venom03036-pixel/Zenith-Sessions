using MacroSuite.Infrastructure.Windows;

namespace MacroSuite.App.Services;

public sealed record HotkeyBindingRequest(string Id, uint Modifiers, uint Vk, Action Callback,
    string Description, bool RequireGlobal = false);

public sealed record HotkeyBindingState(string Id, string Description, HotkeyRegistrationResult Registration)
{
    public string Availability => Registration.Status == HotkeyRegistrationStatus.Success ? "Global"
        : Registration.Status == HotkeyRegistrationStatus.LocalOnly ? "Local-only" : "Unavailable";
}

public sealed partial class GlobalHotkeyService
{
    /// <summary>Acquire new combinations before releasing old ones. A failed required registration
    /// leaves all old callbacks and registrations intact, including swaps between commands.</summary>
    public bool TryReplaceBindings(IReadOnlyList<HotkeyBindingRequest> requests,
        out IReadOnlyList<HotkeyBindingState> states)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            var next = new Dictionary<int, HotkeyBinding>();
            var combos = new HashSet<(uint, uint)>();
            var acquired = new List<int>();
            var report = new List<HotkeyBindingState>(requests.Count);
            bool success = true;
            foreach (var request in requests)
            {
                if (request.Vk == 0 || !combos.Add((request.Modifiers, request.Vk)))
                {
                    report.Add(new(request.Id, request.Description, new(HotkeyRegistrationStatus.ConflictAlreadyBoundInApp,
                        Modifiers: request.Modifiers, Vk: request.Vk, ErrorMessage: "This combination is already assigned in Zenith.")));
                    success = false;
                    break;
                }
                HotkeyBinding binding;
                if (_idByCombo.TryGetValue((request.Modifiers, request.Vk), out int oldId))
                {
                    var old = _hotkeysById[oldId];
                    bool global = old.IsGlobal;
                    if (!global)
                    {
                        global = _registration.Register(_hWnd, old.Id, old.Modifiers | NativeMethods.MOD_NOREPEAT, old.Vk)
                            || _registration.Register(_hWnd, old.Id, old.Modifiers, old.Vk);
                        if (global) acquired.Add(old.Id);
                    }
                    binding = new(old.Id, old.Modifiers, old.Vk, request.Callback, request.Description, global);
                }
                else
                {
                    int id = Interlocked.Increment(ref _currentId);
                    bool global = _registration.Register(_hWnd, id, request.Modifiers | NativeMethods.MOD_NOREPEAT, request.Vk)
                        || _registration.Register(_hWnd, id, request.Modifiers, request.Vk);
                    if (global) acquired.Add(id);
                    binding = new(id, request.Modifiers, request.Vk, request.Callback, request.Description, global);
                }
                var registration = new HotkeyRegistrationResult(binding.IsGlobal ? HotkeyRegistrationStatus.Success : HotkeyRegistrationStatus.LocalOnly,
                    binding.Id, binding.Modifiers, binding.Vk, binding.IsGlobal ? null : "Windows did not grant this global shortcut. It works only while Zenith is focused.");
                report.Add(new(request.Id, request.Description, registration));
                if (request.RequireGlobal && !binding.IsGlobal) { success = false; break; }
                next.Add(binding.Id, binding);
            }
            if (!success)
            {
                foreach (int id in acquired) _registration.Unregister(_hWnd, id);
                states = report;
                return false;
            }
            var removed = _hotkeysById.Values.Where(old => !next.ContainsKey(old.Id)).ToArray();
            _hotkeysById.Clear(); _idByCombo.Clear(); _lastTriggerTicks.Clear();
            foreach (var binding in next.Values)
            {
                _hotkeysById.Add(binding.Id, binding);
                _idByCombo.Add((binding.Modifiers, binding.Vk), binding.Id);
            }
            foreach (var old in removed) if (old.IsGlobal) _registration.Unregister(_hWnd, old.Id);
            states = report;
            return true;
        }
    }
}
