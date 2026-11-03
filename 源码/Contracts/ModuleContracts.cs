using System;
using System.Windows;
using System.Windows.Input;
namespace FileDockDemo.Contracts;

// Internal contracts only. No assembly loading or installable plugin API yet.
internal interface IModuleLifecycle : IDisposable
{
    void OnShown();
    void OnHidden();
}
internal interface IModuleDialog
{
    FrameworkElement View { get; }
    bool HasChanges { get; }
    void OnClosed();
    void FocusInitial();
    void RestoreFocus();
    void RequestClose();
    void HandleWindowKey(KeyEventArgs e);
    void RefreshTheme();
}
internal interface IModuleHost
{
    void ShowDialog(IModuleDialog dialog);
    void CloseDialog(IModuleDialog dialog);
    void ShowStatus(string message);
    bool ConfirmDiscardChanges();
    bool CanRestorePopupFocus { get; }
}
