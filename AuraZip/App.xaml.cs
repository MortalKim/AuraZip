using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.IO;
using System.Linq;
using Windows.ApplicationModel.Activation;
using AuraZip.Core;
using System.Threading.Tasks;

namespace AuraZip;

public partial class App : Application
{
    private MainWindow? _window;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuraZip");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "crash.log"), DateTimeOffset.Now + "\n" + args.Exception + "\n\n");
            }
            catch { }
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Skip(1).SequenceEqual(new[] { OfficePreviewWorker.Argument }))
        {
            _ = RunOfficeWorkerAsync();
            return;
        }
        string? path = null;
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.File && activation.Data is IFileActivatedEventArgs files)
            path = files.Files.FirstOrDefault()?.Path;
        path ??= Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
        var argument = args.Arguments.Trim().Trim('"');
        if (path is null && File.Exists(argument)) path = argument;
        var request = ShellRequest.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        _window = new MainWindow { InitialArchivePath = path, InitialShellAction = request?.Action, InitialShellPaths = request?.Paths };
        _window.Activate();
    }
    private async Task RunOfficeWorkerAsync()
    {
        Console.SetOut(TextWriter.Null);
        try { await OfficePreviewWorker.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput()); }
        finally { Exit(); }
    }
}
