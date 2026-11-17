using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using MultiWave.Services;
using System.Runtime.InteropServices;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MultiWave;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        StartupDiagnostics.Log("App constructor: before InitializeComponent");
        InitializeComponent();
        StartupDiagnostics.Log("App constructor: after InitializeComponent");
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            StartupDiagnostics.Log($"AppDomain unhandled exception: {eventArgs.ExceptionObject}");
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        StartupDiagnostics.Log("OnLaunched: creating MainWindow");
        _window = new MainWindow();
        StartupDiagnostics.Log("OnLaunched: activating MainWindow");
        _window.Activate();
        StartupDiagnostics.Log("OnLaunched: MainWindow activated");
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        StartupDiagnostics.Log($"WinUI unhandled exception (Handled={args.Handled}): {args.Message}\n{args.Exception}\nStack: {args.Exception?.StackTrace}");
        if (args.Exception is COMException { HResult: unchecked((int)0x80040111) })
            args.Handled = true;
    }
}
