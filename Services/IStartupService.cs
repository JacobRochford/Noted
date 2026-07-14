namespace Noted.Services;

public interface IStartupService {
    bool IsRunOnStartupEnabled { get; }
    void SetRunOnStartup(bool enabled);
}
