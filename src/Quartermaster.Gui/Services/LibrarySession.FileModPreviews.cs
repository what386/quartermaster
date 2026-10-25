using Avalonia.Threading;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Gui.Services;

public sealed partial class LibrarySession
{
    private sealed record FileModPreview(string Context, IReadOnlyDictionary<Guid, string[]> Warnings,
        IReadOnlyDictionary<Guid, string> Errors);
    private readonly Dictionary<Guid, FileModPreview> fileModPreviews = [];
    private CancellationTokenSource? fileModPreviewCancellation;
    private Task fileModPreviewTask = Task.CompletedTask;
    private Task fileModPreviewPublication = Task.CompletedTask;
    private long fileModPreviewVersion;
    private string? fileModPreviewSelection;
    private bool fileModPreviewsPaused, fileModPreviewsDisposed;

    private static string PreviewContext(LibraryState state, Profile profile, string game) => game + ":" +
        ProfileFileMods.Signature(state, profile) + ":" + string.Join(";", state.Mods.Where(mod => mod.IsFileBased).Select(mod => mod.Id + ":" + mod.Name));

    private FileModPreview? PreviewFor(Profile profile) => fileModPreviews.TryGetValue(profile.Id, out var preview) &&
        preview.Context == PreviewContext(State, profile, GameDirectory) ? preview : null;

    private void QueueFileModPreviews(bool force = false)
    {
        if (fileModPreviewsDisposed || fileModPreviewsPaused) return;
        var state = State; var game = GameDirectory;
        var profiles = game.Length == 0 ? [] : state.Profiles
            .Where(profile => ProfileFileMods.Signature(state, profile) is not null)
            .OrderByDescending(profile => profile.Id == state.ActiveProfileId).ToArray();
        var selection = Settings.Language + ":" + string.Join("\n", profiles.Select(profile => profile.Id + ":" + PreviewContext(state, profile, game)));
        if (!force && fileModPreviewSelection == selection) return;
        fileModPreviewSelection = selection;
        CancelFileModPreviews();
        var previous = fileModPreviewTask;
        var version = fileModPreviewVersion;
        fileModPreviews.Clear();
        if (profiles.Length == 0) return;
        fileModPreviewCancellation = new();
        var token = fileModPreviewCancellation.Token;
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fileModPreviewPublication = published.Task;
        // The worker chain drains cancelled work before taking the installer's file lock again.
        // Notifications are posted, never awaited, so shutdown can drain workers without a UI deadlock.
        fileModPreviewTask = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try
            {
                await Task.Delay(200, token).ConfigureAwait(false);
                var previews = new Dictionary<Guid, FileModPreview>();
                foreach (var profile in profiles)
                {
                    token.ThrowIfCancellationRequested();
                    var context = PreviewContext(state, profile, game);
                    try
                    {
                        var prepared = await fileMods.PrepareAsync(state, profile, game, token).ConfigureAwait(false);
                        previews[profile.Id] = new(context, prepared.ModWarnings, new Dictionary<Guid, string>());
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Guid? affected = null;
                        for (Exception? cause = ex; cause is not null; cause = cause.InnerException)
                            if (cause.Data["Quartermaster.ModId"] is Guid id) { affected = id; break; }
                        var mods = state.Mods.ToDictionary(mod => mod.Id);
                        var errors = profile.Entries.Where(entry => entry.Enabled && (affected is null || entry.ModId == affected) &&
                                PatchSelection.Select(mods[entry.ModId], entry).Any(set => set.ContentKind == ModContentKind.FileMod))
                            .ToDictionary(entry => entry.ModId, entry => Localizer.Interpolate($"Could not merge {mods[entry.ModId].Name}: {ex.Message}"));
                        previews[profile.Id] = new(context, new Dictionary<Guid, string[]>(), errors);
                    }
                }
                token.ThrowIfCancellationRequested();
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        if (fileModPreviewsDisposed || version != fileModPreviewVersion) return;
                        foreach (var (id, preview) in previews) fileModPreviews[id] = preview;
                        ModDiagnosticsChanged?.Invoke(this, EventArgs.Empty);
                    }
                    finally { published.TrySetResult(); }
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { published.TrySetResult(); }
        });
    }

    /// <summary>Waits for current diagnostics; this does not apply files or authorize deployment.</summary>
    public async Task WaitForFileModPreviewsAsync(CancellationToken ct = default)
    {
        await Task.WhenAll(fileModPreviewTask, fileModPreviewPublication).WaitAsync(ct);
    }

    private void CancelFileModPreviews()
    {
        fileModPreviewVersion++;
        fileModPreviewCancellation?.Cancel();
        fileModPreviewCancellation?.Dispose();
        fileModPreviewCancellation = null;
    }

    private async Task PauseFileModPreviewsAsync()
    {
        fileModPreviewsPaused = true;
        CancelFileModPreviews();
        await fileModPreviewTask;
    }

    private void ResumeFileModPreviews()
    {
        fileModPreviewsPaused = false;
        QueueFileModPreviews(force: true);
    }

    public async ValueTask DisposeAsync()
    {
        fileModPreviewsDisposed = true;
        CancelFileModPreviews();
        await fileModPreviewTask.ConfigureAwait(false);
    }
}
