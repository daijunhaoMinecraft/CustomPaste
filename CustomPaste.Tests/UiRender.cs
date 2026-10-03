using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CustomPaste;
using CustomPaste.Models;
using CustomPaste.Services;

internal static class UiRender
{
    // Off-screen WPF rendering of our own controls, not desktop capture or UI automation.
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        // Pumping the dispatcher also raises Application.Startup. Keep preview isolated from real settings, tray and hotkeys.
        var startup = typeof(App).GetMethod("Application_Startup", BindingFlags.NonPublic | BindingFlags.Instance)!;
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app, startup);
        var storage = new StorageService(Path.Combine(output, "test-data"));
        var log = new LogService(storage.Root, app.Dispatcher);
        var history = new HistoryService(storage);
        using var http = new HttpClient();
        var translation = new TranslationService(http);
        var settings = new AppSettings { Theme = "Light", Backdrop = "None" };
        void Set(string name, object value) => typeof(App).GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.SetValue(app, value);
        Set("Storage", storage); Set("Log", log); Set("History", history); Set("Translator", translation); Set("Settings", settings);
        Set("Paste", new PasteService(translation, history, log));
        using var bindingText = new StringWriter();
        var listener = new TextWriterTraceListener(bindingText);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        var window = new SettingsWindow(app, autoSaveEnabled: false);
        var navigation = (ListBox)window.FindName("Navigation");
        if (window.FindName("CancelKeyBox") is not TextBox cancelKey || cancelKey.GetBindingExpression(TextBox.TextProperty) is null)
            throw new InvalidOperationException("Cancellation hotkey editor must be present and bound.");
        var root = (FrameworkElement)window.Content;
        var names = new[] { "home", "translation", "hotkeys", "history", "logs", "appearance" };
        for (var i = 0; i < names.Length; i++)
        {
            navigation.SelectedIndex = i;
            Render(names[i] + "-light");
        }
        if (!((iNKORE.UI.WPF.Modern.Controls.ToggleSwitch)window.FindName("HistoryToggle")).IsOn)
            throw new InvalidOperationException("History UI must bind to enabled by default.");
        navigation.SelectedIndex = 1;
        ((ComboBox)window.FindName("ProviderBox")).SelectedValue = "DeepLX";
        if (((FrameworkElement)window.FindName("EndpointCard")).Visibility != Visibility.Visible) throw new InvalidOperationException("DeepLX endpoint card must be visible.");
        var endpointBox = window.FindName("EndpointBox") as TextBox ?? throw new InvalidOperationException("DeepLX endpoint must use a plain TextBox.");
        const string exampleEndpoint = "https://example.com/visible-test-token/translate";
        endpointBox.Text = exampleEndpoint;
        ((ComboBox)window.FindName("ProviderBox")).SelectedValue = "AI";
        if (SecretProtector.Unprotect(((AppSettings)window.DataContext).ProtectedDeepLXEndpoint) != exampleEndpoint)
            throw new InvalidOperationException("Plain endpoint edits must still be encrypted in the settings draft.");
        ((ComboBox)window.FindName("ProviderBox")).SelectedValue = "DeepLX";
        if (endpointBox.Text != exampleEndpoint) throw new InvalidOperationException("Endpoint must roundtrip when switching providers.");
        Render("deeplx-light");
        ((ComboBox)window.FindName("ProviderBox")).SelectedValue = "AI";
        if (((FrameworkElement)window.FindName("AISettingsPanel")).Visibility != Visibility.Visible) throw new InvalidOperationException("AI settings must be visible.");
        Render("ai-translation-light");
        settings.Theme = "Dark"; window.ApplyAppearance(); navigation.SelectedIndex = 0; Render("home-dark");
        File.WriteAllText(Path.Combine(output, "binding-diagnostics.txt"), bindingText.ToString());
        PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        log.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Console.WriteLine("UI render files: " + output);
        if (!string.IsNullOrWhiteSpace(bindingText.ToString())) { Console.WriteLine(bindingText); return 1; }
        Console.WriteLine("PASS: six pages instantiated and rendered with no binding diagnostics.");
        return 0;

        void RaiseLoaded(DependencyObject element)
        {
            if (element is FrameworkElement framework) framework.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) RaiseLoaded(VisualTreeHelper.GetChild(element, i));
        }

        void Render(string name)
        {
            root.Measure(new Size(1120, 750)); root.Arrange(new Rect(0, 0, 1120, 750)); root.UpdateLayout();
            RaiseLoaded(root);
            var frame = new System.Windows.Threading.DispatcherFrame();
            var settle = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
            settle.Tick += (_, _) => { settle.Stop(); frame.Continue = false; };
            settle.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            root.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1120, 750, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual();
            using (var drawing = background.RenderOpen())
                drawing.DrawRectangle(new SolidColorBrush(settings.Theme == "Dark" ? Color.FromRgb(32, 32, 32) : Color.FromRgb(243, 243, 243)), null, new Rect(0, 0, 1120, 750));
            bitmap.Render(background);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
        }
    }
}
