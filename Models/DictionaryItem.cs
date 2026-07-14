using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Noted.Models;

public sealed class DictionaryItem : INotifyPropertyChanged
{
    private string _word = "";
    private string _description = "";

    public string Word
    {
        get => _word;
        set
        {
            if (_word != value)
            {
                _word = value;
                OnPropertyChanged();
            }
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            if (_description != value)
            {
                _description = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
