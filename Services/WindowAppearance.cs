using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Noted.Services;

public static class WindowAppearance
{
    private static AppSettingsService? _settings;
    private static readonly Dictionary<Window, FrameworkElement> Targets = new();
    private static bool _registered;

    public static readonly DependencyProperty AlwaysVisibleProperty = DependencyProperty.RegisterAttached(
        "AlwaysVisible", typeof(bool), typeof(WindowAppearance), new PropertyMetadata(false, AlwaysVisibleChanged));

    public static bool GetAlwaysVisible(DependencyObject target) => (bool)target.GetValue(AlwaysVisibleProperty);
    public static void SetAlwaysVisible(DependencyObject target, bool value) => target.SetValue(AlwaysVisibleProperty, value);

    internal static void Initialize(AppSettingsService settings)
    {
        if (_settings is not null) _settings.AppearanceChanged -= SettingsChanged;
        _settings = settings;
        settings.AppearanceChanged += SettingsChanged;
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(WindowLoaded));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(MenuOpened));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler(MenuClosed));
    }

    private static void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || !ReferenceEquals(e.OriginalSource, window) || Targets.ContainsKey(window)) return;
        var target = window is MainWindow ? window.FindName("NotesPanel") as FrameworkElement : window;
        if (target is null) return;
        window.SetCurrentValue(AlwaysVisibleProperty, _settings?.LoadWindowAlwaysVisible(window.GetType().Name) == true);
        Targets.Add(window, target);
        if (window.WindowStyle != WindowStyle.None && window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
            _ = new Noted.Helpers.NativeWindowResizer(window);
        target.MouseEnter += (_, _) => Refresh(window);
        target.MouseLeave += (_, _) => Refresh(window);
        target.IsVisibleChanged += (_, _) => Refresh(window);
        window.Activated += (_, _) => Refresh(window);
        window.Deactivated += (_, _) => Refresh(window);
        window.IsKeyboardFocusWithinChanged += (_, _) => Refresh(window);
        window.IsVisibleChanged += (_, _) => Refresh(window);
        window.AddHandler(ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler((_, _) => SetMenuOpen(window, true)), true);
        window.AddHandler(ContextMenuService.ContextMenuClosingEvent, new ContextMenuEventHandler((_, _) => SetMenuOpen(window, false)), true);
        window.Closed += (_, _) => { Targets.Remove(window); OpenMenus.Remove(window); RefreshAll(); };
        RefreshAll();
    }

    private static readonly HashSet<Window> OpenMenus = new();
    private static void MenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu { PlacementTarget: { } placement } && Window.GetWindow(placement) is Window window)
            SetMenuOpen(window, true);
    }

    private static void MenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu { PlacementTarget: { } placement } && Window.GetWindow(placement) is Window window)
            SetMenuOpen(window, false);
    }

    private static void SetMenuOpen(Window window, bool open)
    {
        if (open) OpenMenus.Add(window); else OpenMenus.Remove(window);
        Refresh(window);
    }

    private static void AlwaysVisibleChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is Window window && Targets.ContainsKey(window))
            _settings?.SaveWindowAlwaysVisible(window.GetType().Name, (bool)e.NewValue);
    }

    private static void SettingsChanged(object? sender, EventArgs e) => RefreshAll();
    private static void RefreshAll()
    {
        foreach (var window in Targets.Keys.ToArray()) Refresh(window);
    }

    internal static void Refresh(Window window)
    {
        if (_settings is null || !Targets.TryGetValue(window, out var target)) return;
        var supportsIdle = window is MainWindow or OverlayWindow or NoteEditorWindow;
        var active = target.IsMouseOver || window.IsActive ||
            OpenMenus.Contains(window) || window.OwnedWindows.Cast<Window>().Any(child => child.IsVisible);
        var idle = supportsIdle && _settings.LoadGhostModeEnabled() && !GetAlwaysVisible(window) && !active;
        var opacity = idle ? _settings.LoadGhostModeOpacity() : _settings.LoadDefaultOpacity();
        // A truly transparent target cannot reliably receive the hover that restores it.
        opacity = Math.Clamp(double.IsFinite(opacity) ? opacity : 0.88, 1.0 / 255, 1);
        target.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(target.Opacity, opacity, TimeSpan.FromMilliseconds(200)));
    }
}
