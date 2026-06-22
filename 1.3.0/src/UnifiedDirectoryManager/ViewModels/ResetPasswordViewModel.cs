using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnifiedDirectoryManager.Models;
using UnifiedDirectoryManager.Services;

namespace UnifiedDirectoryManager.ViewModels;

/// <summary>
/// Backs the reset-password dialog: a new password (entered twice or generated), plus the
/// "must change at next logon" and "unlock" options. Validation happens in <see cref="Commit"/>;
/// the resolved <see cref="Result"/> is read back by the dialog service.
/// </summary>
public partial class ResetPasswordViewModel : ObservableObject
{
    [ObservableProperty] private string _accountTitle = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _confirmPassword = string.Empty;
    [ObservableProperty] private string _generatedPassword = string.Empty;
    [ObservableProperty] private bool _mustChangeAtNextLogon = true;
    [ObservableProperty] private bool _unlock = true;
    [ObservableProperty] private string _error = string.Empty;

    /// <summary>Set once the dialog is committed successfully; null while invalid/cancelled.</summary>
    public PasswordResetRequest? Result { get; private set; }

    /// <summary>Raised when a password is generated so the view can push it into the PasswordBoxes.</summary>
    public event Action<string>? PasswordGenerated;

    [RelayCommand]
    private void GeneratePassword()
    {
        var pwd = PassphraseGenerator.Generate();
        Password = pwd;
        ConfirmPassword = pwd;
        GeneratedPassword = pwd;          // shown read-only so the admin can copy/relay it
        PasswordGenerated?.Invoke(pwd);   // mirror into the (unbindable) PasswordBoxes
    }

    /// <summary>Validates the entries and stores <see cref="Result"/>; returns true if the dialog may close.</summary>
    public bool Commit()
    {
        if (string.IsNullOrEmpty(Password)) { Error = "Enter a new password."; return false; }
        if (!string.Equals(Password, ConfirmPassword, StringComparison.Ordinal))
        {
            Error = "The passwords do not match.";
            return false;
        }
        Result = new PasswordResetRequest(Password, MustChangeAtNextLogon, Unlock);
        return true;
    }
}
