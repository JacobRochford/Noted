namespace Noted.Services;

public interface INotepadProcessService : IDisposable {
    bool IsRunning { get; }
    string? OpenFilePath { get; }
    bool Open(string filePath);
    bool IsFileOpen(string filePath);
    void Minimize();
    void Restore();
    bool TryCloseCurrentNote(int timeoutMilliseconds = 15000);
    void Close();
}
