using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Adv2Obj.App;

public sealed class FileConversionItem : INotifyPropertyChanged
{
    private string _status = "Pending";
    private string _details = string.Empty;

    public required string FileName { get; init; }
    public required string InputPath { get; init; }
    public required string OutputDirectory { get; init; }

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    public string Details
    {
        get => _details;
        set { _details = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
