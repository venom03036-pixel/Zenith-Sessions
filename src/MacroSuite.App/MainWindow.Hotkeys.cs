using System.Windows.Threading;
using MacroSuite.App.Common;
using MacroSuite.App.Services;
using MacroSuite.App.ViewModels;
using MacroSuite.Core.Input;
using MacroSuite.Infrastructure.Input;

namespace MacroSuite.App;

public partial class MainWindow
{
    public void ToggleTransportPause()
    {
        if (DataContext is MainViewModel vm) _transportDispatcher.EnqueueAsync(vm.ToggleTransportPauseAsync);
    }
    private bool _refreshHotkeysPending;
    private bool _applyingHotkeys;
    private bool ApplyHotkeyBindings(MainViewModel vm, IReadOnlyDictionary<string, string>? proposed, bool userChange)
    {
        if (_hotkeyService is null || _applyingHotkeys) return true;
        if (vm.IsAnythingRunning)
        {
            _refreshHotkeysPending = true;
            if (userChange) vm.SettingsVM.ShortcutAvailability = "Finish the current operation before changing shortcuts. Existing shortcuts remain active.";
            return !userChange;
        }
        _applyingHotkeys = true;
        OwnedEmergencyHotkeyListener? replacement = null;
        try
        {
            string Bind(string id, string current) => proposed?.TryGetValue(id, out string? value) == true ? value : current;
            string panic = Bind("panic", vm.SettingsVM.PanicHotkey);
            if (!HotkeyCodec.TryParse(panic, out uint panicMod, out uint panicVk)) return false;
            var requests = new List<HotkeyBindingRequest>();
            var used = new HashSet<(uint, uint)> { (panicMod, panicVk) };
            var reservedLabels = new List<string> { panic };
            var unavailable = new List<string>();
            var changedCombinations = new HashSet<(uint, uint)>();
            if (proposed is not null)
                foreach (string value in proposed.Values)
                    if (HotkeyCodec.TryParse(value, out uint modifiers, out uint key)) changedCombinations.Add((modifiers, key));
            bool invalid = false;
            void Add(string id, string current, Action callback, string label, bool enabled = true)
            {
                string chord = Bind(id, current);
                if (string.IsNullOrWhiteSpace(chord)) return;
                if (!HotkeyCodec.TryParse(chord, out uint modifiers, out uint vk) || !used.Add((modifiers, vk)))
                {
                    unavailable.Add($"{label}: Unavailable ({chord} conflicts with another Zenith shortcut)");
                    if (!userChange || proposed?.ContainsKey(id) == true || changedCombinations.Contains((modifiers, vk))) invalid = true;
                    return;
                }
                reservedLabels.Add(chord);
                if (enabled) requests.Add(new(id, modifiers, vk, callback, label, userChange && proposed?.ContainsKey(id) == true));
            }
            if (vm.CanUseWorkspace)
            {
                Add("record", vm.SettingsVM.RecordHotkey, () => _transportDispatcher.EnqueueAsync(vm.EditorVM.StartRecordingAsync), "Start recording");
                Add("stop", vm.SettingsVM.StopHotkey, () => _transportDispatcher.InterruptAsync(vm.EditorVM.StopMacroAsync), "Stop recording / playback");
                Add("play", vm.SettingsVM.PlayHotkey, () => _transportDispatcher.EnqueueAsync(async () =>
                {
                    if (vm.EditorVM.IsRunning) await vm.EditorVM.StopMacroAsync();
                    else if (vm.EditorVM.RunCommand.CanExecute(null)) await vm.EditorVM.RunMacroAsync();
                }), "Run / stop macro");
                Add("pause", vm.SettingsVM.PauseHotkey, () => _transportDispatcher.EnqueueAsync(vm.ToggleTransportPauseAsync), "Pause / resume");
                Add("autoclicker", vm.SettingsVM.AutoClickerHotkey, () => _transportDispatcher.Enqueue(vm.AutoClickerVM.ToggleRun), "Auto clicker");
                // Kept registered while hidden: callback is a no-op until Mini is visible. Its recovery
                // shortcut must not be released merely because another transport is running.
                Add("mini-click-through", vm.SettingsVM.MiniToolbarClickThroughHotkey,
                    () => Dispatcher.BeginInvoke(() => ToggleMiniToolbarClickThrough()), "Mini click-through");
                if (!used.Contains((0, 0x79))) Add("snip", "F10", () => Dispatcher.BeginInvoke(TriggerGlobalScreenSnip), "Capture image");
                else unavailable.Add("Capture image: Unavailable (F10 is assigned to a transport control)");
                foreach (var script in vm.EditorVM.MacroScripts)
                {
                    if (!HotkeyCodec.TryParse(script.Hotkey, out uint mod, out uint key)) continue;
                    if (!used.Add((mod, key)))
                    {
                        unavailable.Add($"{script.Name}: Unavailable ({script.Hotkey} is already assigned). Its saved shortcut was kept.");
                        if (userChange && changedCombinations.Contains((mod, key))) invalid = true;
                        continue;
                    }
                    var target = script;
                    requests.Add(new("macro-" + script.Id, mod, key,
                        () => _transportDispatcher.EnqueueAsync(isCurrent => vm.EditorVM.ToggleScriptAsync(target, isCurrent)), "Macro: " + script.Name));
                }
            }
            if (userChange && invalid)
            {
                vm.SettingsVM.ShortcutAvailability = string.Join(Environment.NewLine, unavailable) + Environment.NewLine + "Previous shortcuts remain active.";
                return false;
            }
            if (_emergencyHotkeyListener is not { IsRunning: true } || !HotkeyCodec.AreEqual(panic, _activeEmergencyHotkey))
            {
                replacement = new OwnedEmergencyHotkeyListener(panicMod, panicVk,
                    () => { vm.SignalEmergencyStop(); _transportDispatcher.InterruptAsync(vm.TriggerPanic); },
                    () => InputModeGate.IsResourceCapture || IsCapturingAnyBind(vm));
                var observed = replacement;
                observed.UnexpectedlyStopped += () =>
                {
                    if (!ReferenceEquals(_emergencyHotkeyListener, observed)) return;
                    vm.SignalEmergencyStop();
                    _transportDispatcher.InterruptAsync(() =>
                    {
                        vm.TriggerPanic();
                        vm.RefreshEmergencyStopReadiness();
                        vm.SettingsVM.ShortcutAvailability = "Panic shortcut: Unavailable. Its keyboard owner ended unexpectedly. The Panic button remains available; restart Zenith before continuing.";
                    });
                };
                if (!replacement.IsRunning) throw new System.ComponentModel.Win32Exception("The emergency shortcut owner stopped during startup.");
            }
            if (!_hotkeyService.TryReplaceBindings(requests, out var states))
            {
                vm.SettingsVM.ShortcutAvailability = string.Join(Environment.NewLine, states.Select(s => $"{s.Description}: {s.Availability} {s.Registration.ErrorMessage}"))
                    + Environment.NewLine + "The change was cancelled. Previous shortcuts remain active.";
                return false;
            }
            if (replacement is not null)
            {
                var old = _emergencyHotkeyListener; _emergencyHotkeyListener = replacement;
                replacement = null; _activeEmergencyHotkey = panic;
                try { old?.Dispose(); }
                catch (System.ComponentModel.Win32Exception ex)
                { vm.StatusMessage = "The replacement emergency shortcut is active. The previous shortcut reported a cleanup problem: " + ex.Message; }
            }
            vm.SettingsVM.ShortcutAvailability = string.Join(Environment.NewLine,
                new[] { $"Panic stop: Global ({panic})" }.Concat(states.Select(s => $"{s.Description}: {s.Availability} ({HotkeyCodec.Format(s.Registration.Modifiers, s.Registration.Vk)})" )).Concat(unavailable));
            vm.EditorVM.SetReservedTransportHotkeys(reservedLabels);
            var reservedKeys = new List<int>(_hotkeyService.SnapshotVirtualKeys());
            if (panicMod == 0) reservedKeys.Add((int)panicVk);
            vm.EditorVM.ReservedHotkeys.Replace(reservedKeys);
            _refreshHotkeysPending = false;
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            vm.SettingsVM.ShortcutAvailability = "Windows could not apply the shortcut change: " + ex.Message + ". Previous shortcuts remain active.";
            return false;
        }
        finally { replacement?.Dispose(); _applyingHotkeys = false; vm.RefreshEmergencyStopReadiness(); }
    }
}
