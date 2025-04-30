using System.ComponentModel;
using ReimaginedLauncher.HttpClients.Models;

namespace ReimaginedLauncher.Utilities.ViewModels;

public class NexusUserViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private NexusModsValidateResponse? _user;

    public NexusModsValidateResponse? User
    {
        get => _user;
        set
        {
            _user = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(User)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoggedIn)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProfileUrl)));
        }
    }

    public bool IsLoggedIn => User != null;
    public string Name => User?.Name ?? "";
    public string ProfileUrl => User?.ProfileUrl ?? "";
}