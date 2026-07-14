namespace Noted.Services;

public interface IStartupService {
    bool IsRunOnStartupEnabled { get; }
    (bool Success, bool? ActualEnabled, string? Error) SetRunOnStartup(bool enabled);
}
