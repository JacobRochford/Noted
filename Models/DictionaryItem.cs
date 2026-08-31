using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Noted.Models;

public sealed class DictionaryItem : INotifyPropertyChanged
{
    private string _word = "";
    private string _definition = "";

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

    public string Definition
    {
        get => _definition;
        set
        {
            if (_definition != value)
            {
                _definition = value;
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
