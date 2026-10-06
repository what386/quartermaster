using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Quartermaster.Providers.Protocol;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ThemeManager.Apply(ThemePreset.Dark, Avalonia.Media.Color.Parse(ThemeManager.DefaultAccent));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var services = new AppServices(AppServices.DefaultDataDirectory, new DialogService(() => window));
            var viewModel = new MainWindowViewModel(services);
            window.DataContext = viewModel;
            window.Opened += async (_, _) => await viewModel.InitializeAsync();
            var inbox = new NxmInbox(services.DataDirectory);
            var processingLink = false;
            var promptedLinks = new HashSet<string>();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async (_, _) =>
            {
                if (processingLink || !services.Operations.CanInteract || inbox.Pending.Count == 0) return;
                processingLink = true;
                try
                {
                    if (await services.Keys.GetAsync("nexusmods") is null)
                    {
                        var pending = inbox.Pending.FirstOrDefault();
                        if (pending is not null && promptedLinks.Add(pending))
                        {
                            viewModel.Navigate(PageKind.Settings);
                            services.Operations.ReportError(new InvalidOperationException("Add your personal Nexus API key to receive this download."));
                        }
                        return;
                    }
                    var path = inbox.Pending.FirstOrDefault();
                    if (path is null) return;
                    await services.Operations.RunAsync("Receiving Nexus download", async ct =>
                        await services.Providers.HandleNxmAsync(inbox.Read(path), ct));
                    inbox.Remove(path);
                }
                catch (Exception ex) { services.Operations.ReportError(ex); }
                finally { processingLink = false; }
            };
            window.Opened += (_, _) => timer.Start();
            desktop.Exit += (_, _) => { timer.Stop(); services.DisposeAsync().AsTask().GetAwaiter().GetResult(); };
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
