using Microsoft.UI.Xaml;
using MultiWave.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MultiWave;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        StartupDiagnostics.Log("MainWindow: before InitializeComponent");
        InitializeComponent();
        StartupDiagnostics.Log("MainWindow: after InitializeComponent");

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        StartupDiagnostics.Log("MainWindow: title bar configured; navigating to MainPage");

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
        StartupDiagnostics.Log("MainWindow: MainPage navigation requested");
    }
}
