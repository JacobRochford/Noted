using System.Diagnostics;
using System.IO;
using System.Windows;
using Noted.Helpers;
using MessageBox = System.Windows.MessageBox;

namespace Noted.Services;

public sealed class NotepadProcessService : INotepadProcessService {
    private Process? _notepadProcess;
    private string? _openFilePath;

    public bool IsRunning => _notepadProcess is not null && !_notepadProcess.HasExited;
    public string? OpenFilePath => IsRunning ? _openFilePath : null;

    public bool Open(string filePath) {
        if (!IsRunning)
            CleanupClosedProcess();

        if (_openFilePath == filePath && IsRunning)
            return true;

        try {
            var processStartInfo = new ProcessStartInfo {
                FileName = "notepad.exe",
                Arguments = $"\"{filePath}\"",
                UseShellExecute = false
            };
            var process = Process.Start(processStartInfo);
            if (process is not null) {
                if (_notepadProcess is not null && !ReferenceEquals(_notepadProcess, process)) {
                    try { _notepadProcess.Dispose(); } catch { }
                }
                _notepadProcess = process;
                _openFilePath = filePath;
            }
            return process is not null;
        } catch (Exception ex) {
            MessageBox.Show($"Failed to open note:\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    public bool IsFileOpen(string filePath) {
        return IsRunning &&
            !string.IsNullOrWhiteSpace(_openFilePath) &&
            string.Equals(Path.GetFullPath(_openFilePath), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase);
    }

    public void Minimize() {
        if (!IsRunning)
            return;
        var process = _notepadProcess!;
        Task.Run(() =>
        {
            try {
                IntPtr handle = IntPtr.Zero;
                for (int i = 0; i < 20; i++) {
                    process.Refresh();
                    handle = process.MainWindowHandle;
                    if (handle != IntPtr.Zero)
                        break;
                    Thread.Sleep(100);
                }
                if (handle != IntPtr.Zero)
                    WindowInterop.ShowWindow(handle, WindowInterop.SW_HIDE);
            } catch { }
        });
    }

    public void Restore() {
        if (!IsRunning)
            return;
        try {
            _notepadProcess!.Refresh();
            var handle = _notepadProcess.MainWindowHandle;
            if (handle != IntPtr.Zero) {
                WindowInterop.ShowWindow(handle, WindowInterop.SW_SHOW);
                WindowInterop.SetForegroundWindow(handle);
            }
        } catch { }
    }

    public bool TryCloseCurrentNote(int timeoutMilliseconds = 15000) {
        if (!IsRunning) {
            CleanupClosedProcess();
            return true;
        }

        try {
            if (!_notepadProcess!.CloseMainWindow())
                return false;
        } catch {
            return false;
        }

        try {
            if (!_notepadProcess.WaitForExit(timeoutMilliseconds))
                return false;
        } catch {
            return false;
        }

        CleanupClosedProcess();
        return true;
    }

    public void Close() => TryCloseCurrentNote();

    private void CleanupClosedProcess() {
        if (_notepadProcess is not null) {
            try { _notepadProcess.Dispose(); } catch { }
        }

        _notepadProcess = null;
        _openFilePath = null;
    }

    public void Dispose() => Close();
}
