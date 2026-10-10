using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Severino.App.ViewModels;

/// <summary>
/// What the Rotas, Serviços and DNS tabs share: the search box, "Desfazer" after a removal, and
/// a short message at the bottom, optionally with one action, such as going to another tab.
/// </summary>
public abstract partial class ListPageViewModel : ObservableObject
{
    private static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ToastTime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ToastWithActionTime = TimeSpan.FromSeconds(8);

    private readonly DispatcherTimer? _undoTimer;

    /// <summary>Puts back what was removed; returns why it could not, or null.</summary>
    private Func<string?>? _undo;
    private Action? _toastAction;

    protected ListPageViewModel()
    {
        // Outside a running app (tests), there is no dispatcher to time the undo with.
        if (Application.Current is not null)
        {
            _undoTimer = new DispatcherTimer { Interval = UndoWindow };
            _undoTimer.Tick += (_, _) => DismissUndo();
        }
    }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    partial void OnSearchChanged(string value) => OnSearch();

    protected abstract void OnSearch();

    [ObservableProperty]
    public partial string? UndoMessage { get; set; }

    [ObservableProperty]
    public partial string? Toast { get; set; }

    /// <summary>The button next to the message, when it has one.</summary>
    [ObservableProperty]
    public partial string? ToastActionLabel { get; set; }

    protected void ShowUndo(string message, Func<string?> undo)
    {
        _undo = undo;
        UndoMessage = message;
        _undoTimer?.Stop();
        _undoTimer?.Start();
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undo?.Invoke() is { } error)
            ShowToast(error);
        DismissUndo();
    }

    private void DismissUndo()
    {
        _undoTimer?.Stop();
        _undo = null;
        UndoMessage = null;
    }

    protected async void ShowToast(string message, string? actionLabel = null, Action? action = null)
    {
        Toast = message;
        ToastActionLabel = action is null ? null : actionLabel;
        _toastAction = action;
        await Task.Delay(action is null ? ToastTime : ToastWithActionTime);
        if (Toast == message)
            DismissToast();
    }

    [RelayCommand]
    private void ToastAction()
    {
        var action = _toastAction;
        DismissToast();
        action?.Invoke();
    }

    private void DismissToast()
    {
        Toast = null;
        ToastActionLabel = null;
        _toastAction = null;
    }

    protected static string Plural(int count, string one, string many) => count == 1 ? $"1 {one}" : $"{count} {many}";

    protected static void Dispatch(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);
}
