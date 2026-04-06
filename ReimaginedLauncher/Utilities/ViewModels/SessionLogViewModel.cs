using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ReimaginedLauncher.Utilities.ViewModels;

public class SessionLogViewModel : INotifyPropertyChanged
{
    public ObservableCollection<SessionLogEntry> Entries => SessionLogService.Entries;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
