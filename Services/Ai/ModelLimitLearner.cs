using Kil0bitSystemMonitor.Models;

namespace Kil0bitSystemMonitor.Services.Ai;

internal sealed class ModelLimitLearner : IDisposable
{
    private readonly HashSet<string> _attempted = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<string>? _warn;
    private bool _disposed;

    internal ModelLimitLearner(Action<string>? warn = null) => _warn = warn;

    /// <summary>
    /// Called on the settings' owning thread when AI is used. The provider runs in the background;
    /// the result resumes on that thread and applies only to unchanged settings. Each model gets
    /// one attempt per app run, including when a server fails or reports no limits.
    /// </summary>
    internal async Task LearnAsync(AppConfig config, Func<CancellationToken, Task<AiModelList>> listModels, Action save)
    {
        try
        {
            if (!config.AiAssistantEnabled && !config.PadAiEnabled) return;
            string key = ModelCatalog.KeyOf(config);
            if (config.AiModelContext > 0 && string.Equals(config.AiModelLimitsOf, key, StringComparison.Ordinal)) return;

            CancellationToken stop;
            lock (_attempted)
            {
                if (_disposed || !_attempted.Add(key)) return;
                stop = _stop.Token;
            }
            var previous = (config.AiModelLimitsOf, config.AiModelContext, config.AiModelOutput);
            AiModelList? answer = await Task.Run(async () =>
            {
                // The user can change settings before the queued work even starts. ModelCatalog
                // also rechecks the live AI switches immediately before every network send.
                if (stop.IsCancellationRequested || (!config.AiAssistantEnabled && !config.PadAiEnabled) ||
                    !string.Equals(key, ModelCatalog.KeyOf(config), StringComparison.Ordinal)) return null;
                return await listModels(stop).ConfigureAwait(false);
            }, stop);

            if (stop.IsCancellationRequested || answer?.Problem != null || answer == null ||
                (!config.AiAssistantEnabled && !config.PadAiEnabled) ||
                !string.Equals(key, ModelCatalog.KeyOf(config), StringComparison.Ordinal) ||
                previous != (config.AiModelLimitsOf, config.AiModelContext, config.AiModelOutput)) return;

            if (ModelCatalog.Learn(config, answer.Models)) save();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // A callback can contain a URL or server text in its message. Only its type is logged.
            try { _warn?.Invoke("Learning the model's limits failed (" + ex.GetType().Name + ")"); }
            catch (Exception) { }
        }
    }

    public void Dispose()
    {
        lock (_attempted)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            _stop.Dispose();
        }
    }
}
