namespace Noted.Services;

public interface IRunOnStartupService {
    bool IsRunOnStartupEnabled { get; }
    (bool Success, bool? ActualEnabled, string? Error) SetRunOnStartup(bool enabled);
}
