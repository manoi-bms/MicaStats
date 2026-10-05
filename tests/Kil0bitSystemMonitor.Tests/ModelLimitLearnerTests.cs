using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Services.Ai;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class ModelLimitLearnerTests
{
    private static AppConfig Config() => new()
    {
        AiAssistantEnabled = true,
        AiProvider = AiProviders.OpenAiCompatible,
        AiCompatibleBaseUrl = "https://llm.example.com/v1",
        AiCompatibleModel = "example-model",
    };

    private static AiModelList Models(int context = 262_144) =>
        new(new[] { new AiModelInfo("example-model", context, 8192) }, null, "llm.example.com");

    [Fact]
    public async Task Learning_is_nonblocking_once_per_key_and_saves_on_the_callers_dispatcher()
    {
        using var learner = new ModelLimitLearner();
        var config = Config();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<AiModelList>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0, saves = 0;
        await UiThread.RunAsync(async () =>
        {
            int uiThread = Environment.CurrentManagedThreadId;
            Task first = learner.LearnAsync(config, async _ =>
            {
                Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
                Interlocked.Increment(ref requests);
                started.SetResult();
                return await answer.Task;
            }, () =>
            {
                Assert.Equal(uiThread, Environment.CurrentManagedThreadId);
                saves++;
            });
            Assert.False(first.IsCompleted);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await learner.LearnAsync(config, _ => throw new Exception("duplicate request"), () => saves++);
            Assert.Equal(0, config.AiModelContext);
            answer.SetResult(Models());
            await first;
            Assert.Equal(262_144, config.AiModelContext);
            Assert.Equal(8192, config.AiModelOutput);
            Assert.Equal(ModelCatalog.KeyOf(config), config.AiModelLimitsOf);
        });
        Assert.Equal(1, requests);
        Assert.Equal(1, saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_provider_is_asked_with_AI_off_or_limits_already_known(bool known)
    {
        using var learner = new ModelLimitLearner();
        var config = Config();
        if (known) ModelCatalog.Learn(config, Models().Models);
        else config.AiAssistantEnabled = false;
        int requests = 0;
        await learner.LearnAsync(config, _ => { requests++; return Task.FromResult(Models()); }, () => Assert.Fail("saved"));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task MicaPad_alone_can_learn_and_a_new_model_key_gets_its_own_attempt()
    {
        using var learner = new ModelLimitLearner();
        var config = Config();
        config.AiAssistantEnabled = false;
        config.PadAiEnabled = true;
        int requests = 0;
        Task<AiModelList> List(CancellationToken _) { requests++; return Task.FromResult(Models()); }
        await learner.LearnAsync(config, List, () => { });
        config.AiCompatibleModel = "other-model";
        await learner.LearnAsync(config, List, () => { });
        await learner.LearnAsync(config, List, () => { });
        Assert.Equal(2, requests);
        Assert.Equal(AiBudget.Standard, App.BudgetOf(config));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("host")]
    [InlineData("off")]
    [InlineData("dispose")]
    [InlineData("newer-settings")]
    public async Task A_stale_or_cancelled_answer_cannot_replace_the_settings(string change)
    {
        using var learner = new ModelLimitLearner();
        var config = Config();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<AiModelList>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = learner.LearnAsync(config, _ => { started.SetResult(); return answer.Task; }, () => Assert.Fail("saved stale result"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        switch (change)
        {
            case "model": config.AiCompatibleModel = "another"; break;
            case "host": config.AiCompatibleBaseUrl = "https://other.example.com/v1"; break;
            case "off": config.AiAssistantEnabled = false; break;
            case "dispose": learner.Dispose(); break;
            case "newer-settings": ModelCatalog.Learn(config, Models(128_000).Models); break;
        }
        answer.SetResult(Models());
        await pending;
        Assert.Equal(change == "newer-settings" ? 128_000 : 0, config.AiModelContext);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_leaves_settings_unchanged_and_is_not_retried_during_this_run(bool throws)
    {
        using var learner = new ModelLimitLearner();
        var config = Config();
        int requests = 0;
        Task<AiModelList> List(CancellationToken _)
        {
            requests++;
            return throws ? Task.FromException<AiModelList>(new InvalidOperationException("private server detail"))
                : Task.FromResult(new AiModelList(Array.Empty<AiModelInfo>(), "Unavailable.", "llm.example.com"));
        }
        await learner.LearnAsync(config, List, () => Assert.Fail("saved on failure"));
        await learner.LearnAsync(config, List, () => Assert.Fail("saved on failure"));
        Assert.Equal(1, requests);
        Assert.Equal(0, config.AiModelContext);
        Assert.Equal("", config.AiModelLimitsOf);
    }

    [Fact]
    public async Task An_unknown_window_can_be_checked_once_again_in_a_new_run()
    {
        var config = Config();
        ModelCatalog.Learn(config, new[] { new AiModelInfo("example-model", 0, 0) });
        using var learner = new ModelLimitLearner();
        await learner.LearnAsync(config, _ => Task.FromResult(Models()), () => { });
        Assert.Equal(262_144, config.AiModelContext);
    }

    [Fact]
    public void Learning_matches_the_id_exactly_and_is_idempotent()
    {
        var config = Config();
        Assert.False(ModelCatalog.Learn(config, new[] { new AiModelInfo("EXAMPLE-MODEL", 128_000, 4096) }));
        Assert.Equal("", config.AiModelLimitsOf);
        Assert.True(ModelCatalog.Learn(config, Models().Models));
        Assert.False(ModelCatalog.Learn(config, Models().Models));
        Assert.Equal(262_144, config.AiModelContext);
    }
}
