using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace OkakiiLauncher;

/// <summary>
/// macOS Okakii launcher. Handles okakiiclient://join?place=..&ticket=..&year=.. (registered via
/// CFBundleURLTypes in the app bundle), installs/updates the Mac client and starts it with the
/// player's -ticket/-authURL/-scriptURL defaults. Same flow as the Windows launcher, minus the
/// guard (Windows-only).
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new MacOSPlatformOptions { ShowInDock = true })
        .StartWithClassicDesktopLifetime(args);
}

public sealed class App : Application
{
    public const string BaseUrl = "https://www.okakii.lol";
    public const string DownloadBase = "https://okakii.lol/download";
    public static readonly string ClientsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Okakii", "Clients");

    private SplashWindow? window;
    private bool handled;

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            window = new SplashWindow();
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // macOS delivers the clicked okakiiclient:// link as an "open URL" event, not argv.
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
                activatable.Activated += (_, e) =>
                {
                    if (e is ProtocolActivatedEventArgs p && p.Kind == ActivationKind.OpenUri) OnUri(p.Uri);
                };

            var argUri = desktop.Args?.FirstOrDefault(a => a.StartsWith("okakiiclient://", StringComparison.OrdinalIgnoreCase));
            if (argUri != null) OnUri(new Uri(argUri));

            // Opened directly (not from a Play button): give a link event a moment to arrive, then explain.
            DispatcherTimer.RunOnce(() =>
            {
                if (handled) return;
                window.SetStatus("Okakii is set up. Press Play on a game at okakii.lol.");
                window.SetProgress(100);
                DispatcherTimer.RunOnce(() => window.Close(), TimeSpan.FromSeconds(4));
            }, TimeSpan.FromSeconds(2.5));
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OnUri(Uri uri)
    {
        if (handled || window == null) return;
        handled = true;
        var q = ParseQuery(uri.Query);
        q.TryGetValue("place", out var place);
        q.TryGetValue("ticket", out var ticket);
        var year = q.TryGetValue("year", out var y) && !string.IsNullOrEmpty(y) ? y : "2017";
        _ = Task.Run(() => LaunchAsync(window, place, ticket, year));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>();
        foreach (var kv in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = kv.IndexOf('=');
            if (eq > 0) d[kv[..eq]] = Uri.UnescapeDataString(kv[(eq + 1)..].TrimEnd('/'));
        }
        return d;
    }

    private static async Task LaunchAsync(SplashWindow ui, string? place, string? ticket, string year)
    {
        try
        {
            if (string.IsNullOrEmpty(place) || string.IsNullOrEmpty(ticket) || !place.All(char.IsDigit))
            {
                await FinishAsync(ui, "That link is missing its game. Press Play on okakii.lol again.");
                return;
            }
            // Only the 2021 client exists for Mac so far.
            if (year != "2021")
            {
                await FinishAsync(ui, $"This game uses the {year} client, which isn't available on Mac yet.");
                return;
            }

            string folder = Path.Combine(ClientsRoot, year);
            string app = Path.Combine(folder, "RobloxPlayer.app");
            string versionFile = Path.Combine(folder, ".okakii_version");
            string? remote = await GetStringOrNull($"{DownloadBase}/OkakiiClient{year}-mac.version");
            string? local = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : null;

            if (!Directory.Exists(app) || (remote != null && remote != local))
            {
                await InstallAsync(ui, year, folder, remote);
                if (remote != null) File.WriteAllText(versionFile, remote);
            }
            if (!Directory.Exists(app))
            {
                await FinishAsync(ui, "Couldn't install the Okakii client.");
                return;
            }

            ui.SetStatus("Starting Okakii ...");
            ui.SetProgress(-1);
            string joinUrl = $"{BaseUrl}/game/PlaceLauncher.ashx?placeid={place}&ticket={ticket}&{year}=true";
            var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            foreach (var a in new[] { "-n", "-a", app, "--args",
                         "-ticket", ticket, "-authURL", $"{BaseUrl}/Login/Negotiate.ashx", "-scriptURL", joinUrl })
                psi.ArgumentList.Add(a);
            Process.Start(psi)?.WaitForExit(15000);

            await Task.Delay(1500);
            Dispatcher.UIThread.Post(() => ui.Close());
        }
        catch (Exception ex)
        {
            await FinishAsync(ui, "Error: " + ex.Message);
        }
    }

    private static async Task InstallAsync(SplashWindow ui, string year, string folder, string? version)
    {
        ui.SetStatus("Downloading Okakii ...");
        ui.SetProgress(0);
        Directory.CreateDirectory(folder);
        string tmp = Path.Combine(Path.GetTempPath(), $"OkakiiClient{year}-{Guid.NewGuid():N}.zip");
        string url = $"{DownloadBase}/OkakiiClient{year}-mac.zip?v={Uri.EscapeDataString(version ?? DateTime.UtcNow.Ticks.ToString())}";

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
        using (var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            res.EnsureSuccessStatusCode();
            long total = res.Content.Headers.ContentLength ?? -1;
            await using var src = await res.Content.ReadAsStreamAsync();
            await using var dst = File.Create(tmp);
            var buf = new byte[1 << 16];
            long got = 0; int lastPct = -1, n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                got += n;
                if (total > 0)
                {
                    int pct = (int)(got * 100 / total);
                    if (pct != lastPct) { lastPct = pct; ui.SetProgress(pct); ui.SetStatus($"Downloading Okakii ... {pct}%"); }
                }
            }
        }

        ui.SetStatus("Installing Okakii ...");
        ui.SetProgress(-1);
        string app = Path.Combine(folder, "RobloxPlayer.app");
        if (Directory.Exists(app)) Directory.Delete(app, true);
        try
        {
            // ditto keeps the bundle's exec bits and symlinks (ZipFile would drop them).
            Run("/usr/bin/ditto", "-x", "-k", tmp, folder);
        }
        finally { try { File.Delete(tmp); } catch { } }
        // Downloaded from the web: clear quarantine, and re-sign ad hoc because the patched
        // binary no longer matches Roblox's signature.
        Run("/usr/bin/xattr", "-cr", app);
        Run("/usr/bin/codesign", "--force", "--deep", "-s", "-", app);
    }

    private static void Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception($"{Path.GetFileName(exe)} failed: {p.StandardError.ReadToEnd().Trim()}");
    }

    private static async Task<string?> GetStringOrNull(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            return (await http.GetStringAsync(url)).Trim();
        }
        catch { return null; }
    }

    private static async Task FinishAsync(SplashWindow ui, string message)
    {
        ui.SetStatus(message);
        ui.SetProgress(100);
        await Task.Delay(5000);
        Dispatcher.UIThread.Post(() => ui.Close());
    }
}

/// <summary>The classic launcher dialog: logo, status line, green progress bar.</summary>
public sealed class SplashWindow : Window
{
    private readonly TextBlock status;
    private readonly ClassicBar bar;

    public SplashWindow()
    {
        Title = "Okakii";
        Width = 520;
        Height = 320;
        CanResize = false;
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.White;

        var logo = new Image
        {
            Width = 104,
            Height = 104,
            Source = new Bitmap(AssetLoader.Open(new Uri("avares://OkakiiLauncher/Assets/logo.png"))),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 58, 0, 0),
        };
        status = new TextBlock
        {
            Text = "Connecting to Okakii ...",
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(34, 34, 34)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 34, 20, 0),
        };
        bar = new ClassicBar { Width = 460, Height = 20, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { logo, status, bar } },
        };
        PointerPressed += (_, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Escape) Close(); };
    }

    public void SetStatus(string text) => Dispatcher.UIThread.Post(() => status.Text = text);
    public void SetProgress(int percent) => Dispatcher.UIThread.Post(() => bar.Value = percent);
}

/// <summary>Light sunken track with a green block; Value -1 sweeps (marquee), 0..100 fills.</summary>
public sealed class ClassicBar : Control
{
    private static readonly IBrush Track = new SolidColorBrush(Color.FromRgb(230, 230, 230));
    private static readonly IPen Edge = new Pen(new SolidColorBrush(Color.FromRgb(188, 188, 188)), 1);
    private static readonly IBrush Green = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromRgb(18, 138, 18), 0), new GradientStop(Color.FromRgb(6, 112, 6), 1) },
    };

    private readonly DispatcherTimer timer;
    private double pos;
    private int value = -1;

    public ClassicBar()
    {
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            pos += 0.012;
            if (pos > 1) pos = -0.28;
            InvalidateVisual();
        });
        timer.Start();
    }

    public int Value
    {
        get => value;
        set
        {
            this.value = Math.Clamp(value, -1, 100);
            if (this.value < 0) timer.Start(); else timer.Stop();
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        ctx.FillRectangle(Track, r);
        var inner = r.Deflate(1);
        Rect block = value >= 0
            ? new Rect(inner.X, inner.Y, inner.Width * value / 100.0, inner.Height)
            : new Rect(inner.X + inner.Width * pos, inner.Y, inner.Width * 0.28, inner.Height).Intersect(inner);
        if (block.Width > 0) ctx.FillRectangle(Green, block);
        ctx.DrawRectangle(Edge, r.Deflate(0.5));
    }
}
