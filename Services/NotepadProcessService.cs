using System.Diagnostics;
using System.Windows;
using MyNotes.Helpers;
using MessageBox = System.Windows.MessageBox;

namespace MyNotes.Services;

public sealed class NotepadProcessService : IDisposable {
    private Process? _notepadProcess;
    private string? _openFilePath;

    public bool IsRunning => _notepadProcess is not null && !_notepadProcess.HasExited;

    public void Open(string filePath) {
        if (_openFilePath == filePath && IsRunning) return;

        if (IsRunning) {
            try { _notepadProcess!.CloseMainWindow(); } catch { }
            _notepadProcess = null;
        }

        try {
            var psi = new ProcessStartInfo("notepad.exe", $"\"{filePath}\"") {
                UseShellExecute = false
            };
            var process = Process.Start(psi);
            if (process is not null) {
                _notepadProcess = process;
                _openFilePath = filePath;
                Task.Run(() => {
                    try { process.WaitForInputIdle(); } catch { }
                });
            }
        } catch (Exception ex) {
            MessageBox.Show($"Failed to open note:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void Minimize() {
        if (!IsRunning) return;
        Task.Run(() => {
            try {
                IntPtr handle = IntPtr.Zero;
                for (int i = 0; i < 20; i++) {
                    handle = WindowInterop.FindWindow("Notepad", null);
                    if (handle != IntPtr.Zero) break;
                    Thread.Sleep(100);
                }
                if (handle != IntPtr.Zero)
                    WindowInterop.ShowWindow(handle, WindowInterop.SW_HIDE);
            } catch { }
        });
    }

    public void Restore() {
        if (!IsRunning) return;
        try {
            var handle = WindowInterop.FindWindow("Notepad", null);
            if (handle != IntPtr.Zero) {
                WindowInterop.ShowWindow(handle, WindowInterop.SW_SHOW);
                WindowInterop.SetForegroundWindow(handle);
            }
        } catch { }
    }

    public void Close() {
        if (IsRunning) {
            try { _notepadProcess!.CloseMainWindow(); } catch { }
            _notepadProcess?.Dispose();
            _notepadProcess = null;
            _openFilePath = null;
        }
    }

    public void Dispose() => Close();
}
