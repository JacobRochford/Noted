using System.Windows;
using System.Windows.Threading;

namespace Noted.Tests.Services;

internal static class WpfTestHost
{
    private static readonly Lazy<Task<Application>> s_application = new(CreateApplication);

    internal static void Invoke(Action action)
    {
        var app = s_application.Value.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        void Execute()
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            action();
        }

        if (app.Dispatcher.CheckAccess())
            Execute();
        else
            app.Dispatcher.InvokeAsync(Execute).Task.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
    }

    private static Task<Application> CreateApplication()
    {
        if (Application.Current is { } existing)
        {
            if (existing.GetType() != typeof(Application))
                throw new InvalidOperationException("The WPF test host requires a plain Application without production startup behavior.");
            existing.Dispatcher.Invoke(() => LoadResources(existing));
            return Task.FromResult(existing);
        }

        var ready = new TaskCompletionSource<Application>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                LoadResources(app);
                ready.SetResult(app);
            }
            catch (Exception ex)
            {
                ready.SetException(ex);
                return;
            }

            Dispatcher.Run();
        }) { IsBackground = true, Name = "Noted WPF test host" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    private static void LoadResources(Application app)
    {
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Noted;component/Resources/ApplicationResources.xaml", UriKind.Relative)
        });
    }
}
