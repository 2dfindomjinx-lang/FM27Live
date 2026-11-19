using System.Diagnostics;
using System.Text.Json;

namespace Fm27Editor.App.Live;

public sealed record InstallRequest(int ParentPid, long ParentStarted, string Target, string Stage, string Nonce);

public static class LiveInstaller
{
    public static bool CanSelfUpdate => Environment.ProcessPath is { } exe &&
        Path.GetFileName(exe).Equals(LiveFeed.AssetName, StringComparison.OrdinalIgnoreCase);

    public static void Start(StagedUpdate staged)
    {
        if (!CanSelfUpdate) throw new InvalidOperationException("Otomatik kurulum yalnız FM27Live.exe paketinde kullanılabilir.");
        string target = Environment.ProcessPath!;
        // Test writability before closing the editor. Program Files copies report an actionable error.
        string probe = Path.Combine(Path.GetDirectoryName(target)!, ".fm27live-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { }
        File.Delete(probe);
        string nonce = Guid.NewGuid().ToString("N");
        string folder = Path.Combine(LiveFeed.StageRoot, "installer-" + nonce);
        Directory.CreateDirectory(folder);
        string helper = Path.Combine(folder, "FM27Live.Updater.exe");
        File.Copy(target, helper);
        var parent = Process.GetCurrentProcess();
        var request = new InstallRequest(parent.Id, parent.StartTime.ToUniversalTime().Ticks, target, staged.Folder, nonce);
        string requestPath = Path.Combine(folder, "request.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        start.ArgumentList.Add("--apply-live-update");
        start.ArgumentList.Add(requestPath);
        _ = Process.Start(start) ?? throw new IOException("Güncelleme yardımcısı başlatılamadı.");
    }

    public static async Task<int> ApplyAsync(string requestFile)
    {
        InstallRequest? request = null;
        string? backup = null;
        bool replaced = false;
        bool validRequestPaths = false;
        Process? child = null;
        try
        {
            request = JsonSerializer.Deserialize<InstallRequest>(await File.ReadAllTextAsync(requestFile)) ?? throw new InvalidDataException("Kurulum isteği geçersiz.");
            string target = Path.GetFullPath(request.Target);
            string stage = Path.GetFullPath(request.Stage);
            if (Path.GetFileName(target) != LiveFeed.AssetName || !Guid.TryParseExact(request.Nonce, "N", out _) ||
                !stage.StartsWith(Path.GetFullPath(LiveFeed.StageRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Kurulum yolu geçersiz.");
            validRequestPaths = true;
            var manifest = LiveFeed.Verify(await File.ReadAllTextAsync(Path.Combine(stage, "update.json")), LiveFeed.PublicKey);
            if (manifest.Version != Path.GetFileName(stage) || Version.Parse(manifest.Version) <= LiveFeed.Current)
                throw new InvalidDataException("Eski veya yanlış güncelleme reddedildi.");
            string source = Path.Combine(stage, LiveFeed.AssetName);
            await LiveFeed.VerifyFileAsync(source, manifest);
            try
            {
                using var parent = Process.GetProcessById(request.ParentPid);
                if (parent.StartTime.ToUniversalTime().Ticks == request.ParentStarted)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    await parent.WaitForExitAsync(timeout.Token);
                }
            }
            catch (ArgumentException) { }
            string pending = target + ".pending";
            backup = target + ".previous";
            File.Copy(source, pending, true);
            await LiveFeed.VerifyFileAsync(pending, manifest);
            AtomicReplace(target, pending, backup);
            replaced = true;
            string healthFile = Path.Combine(Path.GetDirectoryName(requestFile)!, "healthy.txt");
            if (File.Exists(healthFile)) File.Delete(healthFile);
            var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
            start.ArgumentList.Add("--live-health"); start.ArgumentList.Add(healthFile); start.ArgumentList.Add(request.Nonce);
            child = Process.Start(start) ?? throw new IOException("Yeni sürüm başlatılamadı.");
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(500);
                if (File.Exists(healthFile) && await File.ReadAllTextAsync(healthFile) == request.Nonce)
                {
                    await File.WriteAllTextAsync(Path.Combine(LiveFeed.DataRoot, "last-update.txt"), $"{DateTimeOffset.Now:O} — {manifest.Version} kuruldu.");
                    return 0;
                }
                if (child.HasExited) break;
            }
            throw new IOException("Yeni sürüm açılış kontrolünü tamamlayamadı; önceki sürüme dönülüyor.");
        }
        catch (Exception ex)
        {
            // Suppress retries even when copying/replacing fails before the new exe is installed.
            // Otherwise every launch would close the working editor and retry the same blocked package.
            if (validRequestPaths && request is not null)
            {
                try { await File.WriteAllTextAsync(Path.Combine(request.Stage, "failed.txt"), ex.Message); }
                catch (Exception markerError) when (markerError is IOException or UnauthorizedAccessException) { }
            }
            if (replaced && request is not null && backup is not null)
            {
                // Only terminate the newly launched editor process; never the game or the old editor.
                try { if (child is { HasExited: false }) { child.Kill(); await child.WaitForExitAsync(); } } catch (Exception) { }
                try
                {
                    File.Move(request.Target, request.Target + ".failed", true);
                    File.Move(backup, request.Target);
                    Process.Start(new ProcessStartInfo(request.Target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(request.Target)! });
                }
                catch (Exception recovery) { ex = new AggregateException(ex, recovery); }
            }
            else if (validRequestPaths && request is not null && File.Exists(request.Target) && !ParentStillRunning(request))
            {
                try { Process.Start(new ProcessStartInfo(request.Target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(request.Target)! }); }
                catch (Exception restartError) { ex = new AggregateException(ex, restartError); }
            }
            Directory.CreateDirectory(LiveFeed.DataRoot);
            await File.WriteAllTextAsync(Path.Combine(LiveFeed.DataRoot, "update-error.txt"), ex.ToString());
            return 1;
        }
        finally { child?.Dispose(); }
    }

    public static void AtomicReplace(string target, string pending, string backup)
    {
        // File.Replace is atomic on Windows and retains the previous binary even after power loss.
        File.Replace(pending, target, backup, true);
    }

    static bool ParentStillRunning(InstallRequest request)
    {
        try
        {
            using var parent = Process.GetProcessById(request.ParentPid);
            return !parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == request.ParentStarted;
        }
        catch (ArgumentException) { return false; }
    }

    public static void ReportHealthy(string[] args)
    {
        if (args.Length != 3 || args[0] != "--live-health" || !Guid.TryParseExact(args[2], "N", out _)) return;
        string path = Path.GetFullPath(args[1]);
        string expected = Path.Combine(Path.GetFullPath(LiveFeed.StageRoot), "installer-" + args[2], "healthy.txt");
        if (path.Equals(expected, StringComparison.OrdinalIgnoreCase)) File.WriteAllText(path, args[2]);
    }
}
