using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MacroSuite.App.ViewModels;

namespace MacroSuite.App.Views;

public partial class MacroEditorView : UserControl
{
    private bool _programmaticActionScroll;
    private MacroEditorViewModel? _subscribedVm;
    private DispatcherOperation? _pendingActionScroll;
    private object? _pendingScrollItem;

    public MacroEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => HookViewModel(DataContext as MacroEditorViewModel);
        Unloaded += (_, _) => HookViewModel(null);
        ActionSequenceList.PreviewMouseWheel += (_, _) => BeginManualActionScroll();
        ActionSequenceList.AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler((_, e) =>
        {
            if (e.ScrollEventType != ScrollEventType.EndScroll) BeginManualActionScroll();
        }), true);
        ActionSequenceList.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => BeginManualActionScroll()), true);
    }

    private void AddMoreActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
            return;

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.DataContext = DataContext;
        button.ContextMenu.IsOpen = true;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        HookViewModel(e.NewValue as MacroEditorViewModel);
    }

    private void HookViewModel(MacroEditorViewModel? vm)
    {
        _pendingActionScroll?.Abort(); _pendingActionScroll = null; _pendingScrollItem = null;
        if (_subscribedVm != null)
        {
            _subscribedVm.CommitVisibleRecordingFields = null;
            _subscribedVm.RecordingBatchApplied -= OnRecordingBatchApplied;
            _subscribedVm.StepNavigationRequested -= OnStepNavigationRequested;
            _subscribedVm.PropertyChanged -= OnEditorPropertyChanged;
        }

        _subscribedVm = vm;
        if (_subscribedVm != null)
        {
            _subscribedVm.CommitVisibleRecordingFields = CommitRecordingFields;
            _subscribedVm.RecordingBatchApplied += OnRecordingBatchApplied;
            _subscribedVm.StepNavigationRequested += OnStepNavigationRequested;
            _subscribedVm.PropertyChanged += OnEditorPropertyChanged;
            ApplyActionEditorLayout();
            LibraryColumn.Width = new GridLength(_subscribedVm.LibraryPaneWidth);
        }
    }

    private void OnRecordingBatchApplied()
    {
        if (!IsVisible) return;
        if (_subscribedVm is not { IsRecording: true, IsRecordingFollowEnabled: true })
            return;
        if (ActionSequenceList.Items.Count == 0)
            return;

        QueueActionScroll(ActionSequenceList.Items[^1]);
    }

    private void OnStepNavigationRequested(MacroStepItemViewModel? step)
    {
        if (!IsVisible || step is null)
            return;
        QueueActionScroll(step);
    }

    private void QueueActionScroll(object item)
    {
        _pendingScrollItem = item;
        if (_pendingActionScroll is { Status: DispatcherOperationStatus.Pending }) return;
        _pendingActionScroll = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            var target = _pendingScrollItem; _pendingScrollItem = null; _pendingActionScroll = null;
            if (!IsVisible || target == null) return;
            _programmaticActionScroll = true;
            try { ActionSequenceList.ScrollIntoView(target); }
            finally { _programmaticActionScroll = false; }
        }));
    }

    private void BeginManualActionScroll()
    {
        // A user's scrollbar gesture wins over queued playback/recording follow.
        _pendingActionScroll?.Abort(); _pendingActionScroll = null; _pendingScrollItem = null;
        if (_subscribedVm is not { } vm) return;
        if (vm.IsRecording) vm.NotifyUserStoppedRecordingFollow();
        if (vm.IsRunning && vm.FollowCurrentAction) vm.FollowCurrentAction = false;
    }

    private void ActionSequenceList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_programmaticActionScroll || _subscribedVm is not { IsRecording: true, IsRecordingFollowEnabled: true })
            return;
        if (e.ExtentHeightChange != 0 && e.VerticalChange == 0)
            return;
        if (e.VerticalChange >= 0)
            return;

        _subscribedVm.NotifyUserStoppedRecordingFollow();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MacroEditorViewModel.LibraryPaneWidth) && _subscribedVm is not null)
            LibraryColumn.Width = new GridLength(_subscribedVm.LibraryPaneWidth);
        if (e.PropertyName is nameof(MacroEditorViewModel.ShowActionEditor)
            or nameof(MacroEditorViewModel.ActionEditorWidth)
            or nameof(MacroEditorViewModel.AutoSizeActionEditor))
            ApplyActionEditorLayout();
    }

    private void ActionWorkspaceGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            ApplyActionEditorLayout();
    }

    /// <summary>
    /// Editor width follows the window (about a third of the workspace, clamped) so every
    /// field stays visible without enlarging the window. Dragging the splitter pins a
    /// manual width instead.
    /// </summary>
    private void ApplyActionEditorLayout()
    {
        if (_subscribedVm is null || EditorColumn is null || ActionEditorPanel is null || EditorSplitter is null)
            return;

        if (!_subscribedVm.ShowActionEditor)
        {
            EditorColumn.Width = new GridLength(0);
            ActionEditorPanel.Visibility = Visibility.Collapsed;
            EditorSplitter.Visibility = Visibility.Collapsed;
            return;
        }

        double width = _subscribedVm.ActionEditorWidth;
        if (_subscribedVm.AutoSizeActionEditor)
        {
            double available = ActionWorkspaceGrid?.ActualWidth ?? 0;
            if (available > 0)
                width = Math.Clamp(available * 0.34, 300, 340);
        }

        // Keep at least 180 device-independent pixels for the sequence at narrow sizes.
        double workspaceWidth = ActionWorkspaceGrid?.ActualWidth ?? 0;
        if (workspaceWidth > 0) width = Math.Min(width, Math.Max(180, workspaceWidth - 185));

        EditorColumn.Width = new GridLength(width);
        ActionEditorPanel.Visibility = Visibility.Visible;
        EditorSplitter.Visibility = Visibility.Visible;
    }

    private void EditorSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_subscribedVm is null || !_subscribedVm.ShowActionEditor)
            return;
        if (EditorColumn.ActualWidth >= 220)
        {
            _subscribedVm.AutoSizeActionEditor = false;
            _subscribedVm.ActionEditorWidth = EditorColumn.ActualWidth;
        }
    }

    private void LibrarySplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_subscribedVm is not null) _subscribedVm.LibraryPaneWidth = LibraryColumn.ActualWidth;
    }

    private void EditorRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item) { item.IsSelected = true; item.Focus(); }
    }

    private void HotkeyInputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MacroEditorViewModel vm) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        if (key == Key.Tab)
        {
            return;
        }

        if ((key is Key.Back or Key.Delete) && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            vm.TryChangeHotkey(string.Empty);
            return;
        }

        var hotkeyStr = MainWindow.GetHotkeyString(e);
        if (!string.IsNullOrEmpty(hotkeyStr))
        {
            e.Handled = true;
            vm.TryChangeHotkey(hotkeyStr);
        }
    }

    private void HotkeyInputBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is MacroEditorViewModel vm)
            vm.IsScriptHotkeyBoxFocused = true;
    }

    private void HotkeyInputBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is MacroEditorViewModel vm)
        {
            vm.IsScriptHotkeyBoxFocused = false;
            if (sender is TextBox tb)
                tb.Text = vm.CurrentHotkey;
        }
    }

    private void ScriptCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is MacroScriptItemViewModel s)
        {
            if (DataContext is MacroEditorViewModel vm)
            {
                vm.SelectedScript = s;
            }
        }
    }

    private void ActionSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MacroEditorViewModel vm)
            return;

        if (e.Key == Key.Enter)
        {
            vm.FindActionMatch(forward: Keyboard.Modifiers != ModifierKeys.Shift);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            vm.ClearActionSearchCommand.Execute(null);
            ActionSequenceList.Focus();
            e.Handled = true;
        }
    }

    private void MacroEditorView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MacroEditorViewModel vm) return;

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ActionSearchBox.Focus();
            ActionSearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (vm.ActionEditor.IsCapturingKey)
        {
            var captureKey = e.Key == Key.System ? e.SystemKey : e.Key;
            if (captureKey == Key.Escape)
            {
                vm.ActionEditor.IsCapturingKey = false;
            }
            else if (captureKey is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift))
            {
                vm.ActionEditor.TryCaptureKey(KeyInterop.VirtualKeyFromKey(captureKey));
            }

            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is TextBox or PasswordBox) return;

        bool actionListFocused = IsInActionSequenceList(Keyboard.FocusedElement as DependencyObject);
        if (actionListFocused || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Key == Key.Home && (actionListFocused || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
            {
                vm.JumpToTop();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.End && (actionListFocused || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
            {
                vm.JumpToBottom();
                e.Handled = true;
                return;
            }
        }

        if (actionListFocused)
        {
            if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
            {
                vm.RemoveStepCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (e.Key is Key.Enter or Key.F2)
            {
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.D)
                {
                    vm.DuplicateStepCommand.Execute(null);
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Up)
                {
                    vm.MoveUpCommand.Execute(null);
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Down)
                {
                    vm.MoveDownCommand.Execute(null);
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Z)
                {
                    vm.UndoEditCommand.Execute(null);
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Y)
                {
                    vm.RedoEditCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
            }

            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            vm.UndoEditCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
        {
            vm.RedoEditCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z)
        {
            vm.RedoEditCommand.Execute(null);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Up:
                vm.SelectPreviousMacro();
                e.Handled = true;
                break;
            case Key.Down:
                vm.SelectNextMacro();
                e.Handled = true;
                break;
            case Key.Enter:
                vm.PlaySelectedMacroCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Space:
                vm.TogglePlayPauseMacroCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete:
                vm.DeleteSelectedMacroCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                vm.SelectedStep = null;
                e.Handled = true;
                break;
        }
    }

    private bool IsInActionSequenceList(DependencyObject? origin)
    {
        while (origin != null)
        {
            if (ReferenceEquals(origin, ActionSequenceList))
                return true;
            origin = VisualTreeHelper.GetParent(origin);
        }

        return false;
    }

    private void ActionTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MacroEditorViewModel vm)
            vm.ActionEditor.CommitKindChange();
    }

    private void ActionEditorApply_Click(object sender, RoutedEventArgs e)
    {
        MacroSuite.App.Common.EditorBindingFlush.FlushTextBoxes(ActionEditorPanel);
    }
}
