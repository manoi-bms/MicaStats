using System;
using System.Collections.Generic;
using System.Threading;
using Kil0bitSystemMonitor.Services.Ai;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// One question to the assistant: the updates it streams back. <see cref="AiAssistant.AskAsync"/>
    /// has exactly this shape; tests pass a scripted stream instead.
    /// </summary>
    public delegate IAsyncEnumerable<AssistantUpdate> AskStream(AiConversation conversation, string question, CancellationToken ct);

    /// <summary>
    /// What one Send needs, built fresh for each question so a provider, model or key change
    /// applies from the next one: a way to ask, or the sentence saying why there is none.
    /// </summary>
    /// <param name="Ask">Asks one question; null when <paramref name="Problem"/> says why not.</param>
    /// <param name="Problem">A user-facing sentence such as "Add an API key in Settings > AI.".</param>
    /// <param name="Resource">Disposed once the answer ends: the provider client built for this question.</param>
    public sealed record AskSetup(AskStream? Ask, string? Problem, IDisposable? Resource = null);
}
