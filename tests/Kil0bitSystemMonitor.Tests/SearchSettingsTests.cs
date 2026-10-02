using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Kil0bitSystemMonitor.Models;
using Kil0bitSystemMonitor.Pad;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Search;
using Xunit;

namespace Kil0bitSystemMonitor.Tests
{
    public class SearchSettingsTests
    {
        [Fact]
        public void Servers_are_kept_normalized_or_empty()
        {
            var config = new AppConfig();
            config.PadEmbeddingServer = " http://gpu:8000/v1/ ";
            config.PadRerankServer = "not a server";
            Assert.Equal("http://gpu:8000/v1", config.PadEmbeddingServer);
            Assert.Equal("", config.PadRerankServer);
            config.PadEmbeddingModel = "  bge-m3 ";
            Assert.Equal("bge-m3", config.PadEmbeddingModel);
        }

        [Fact]
        public void Everything_is_off_by_default()
        {
            var config = new AppConfig();
            Assert.False(config.PadSemanticSearch);
            Assert.False(config.PadRerank);
            Assert.Equal(SearchSettings.Off, PadSearchSettings.From(config, _ => null));
        }

        [Fact]
        public void Settings_carry_the_saved_keys_and_drop_servers_without_an_address()
        {
            var config = new AppConfig
            {
                PadSemanticSearch = true, PadEmbeddingServer = "http://gpu/v1", PadEmbeddingModel = "m",
                PadRerank = true, PadRerankServer = "",
            };
            var settings = PadSearchSettings.From(config, name => name == SecretNames.PadEmbeddingKey ? "sk-e" : null);

            Assert.Equal(new SearchServer("http://gpu/v1", "m", "sk-e"), settings.Embedding);
            Assert.Null(settings.Reranker);
            Assert.True(settings.CanEmbed);
            Assert.False(settings.CanRerank);
        }

        [Fact]
        public void A_server_never_prints_its_key()
        {
            var server = new SearchServer("http://gpu/v1", "m", "sk-secret");

            Assert.DoesNotContain("sk-secret", server.ToString(), StringComparison.Ordinal);
            Assert.Contains("Key = ***", server.ToString(), StringComparison.Ordinal);
            Assert.Contains("BaseUrl = http://gpu/v1", server.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("sk-secret", new SearchSettings(true, server, true, server).ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("***", (server with { Key = null }).ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void Keys_never_reach_the_config_file()
        {
            var config = new AppConfig { PadEmbeddingServer = "http://gpu/v1" };
            Assert.Null(typeof(AppConfig).GetProperty("PadEmbeddingKey"));
            Assert.Null(typeof(AppConfig).GetProperty("PadRerankKey"));
            Assert.DoesNotContain(typeof(AppConfig).GetProperties(), p => p.Name.StartsWith("Pad", StringComparison.Ordinal) && p.Name.EndsWith("Key", StringComparison.Ordinal));
        }

        [Fact]
        public void The_workspace_tells_about_edits_renames_and_deletes() => UiThread.Run(() =>
        {
            using var env = new PadTestEnv();
            var changed = new System.Collections.Generic.List<string>();
            var deleted = new System.Collections.Generic.List<string>();
            env.Workspace.NoteTextChanged += n => changed.Add(n.Id);
            env.Workspace.NoteDeleted += deleted.Add;

            var note = env.Workspace.NewNote();
            env.Workspace.NotifyChanged(note);
            env.Workspace.Rename(note, "Named");
            Assert.Equal(new[] { note.Id, note.Id }, changed.ToArray());

            note.TextProvider = () => "text";
            env.Workspace.NotifyChanged(note);
            env.Workspace.FlushAll(TimeSpan.FromSeconds(2));
            env.Workspace.Close(note);
            env.Workspace.FlushAll(TimeSpan.FromSeconds(2));
            Assert.True(env.Workspace.DeleteClosed(note.Id));
            Assert.Equal(new[] { note.Id }, deleted.ToArray());
        });

        [Fact]
        public void The_feeder_indexes_open_and_closed_notes_and_follows_edits() => UiThread.Run(() =>
        {
            using var env = new PadTestEnv();
            var open = env.Workspace.NewNote();
            open.TextProvider = () => "open words";
            env.Workspace.NotifyChanged(open);
            env.Workspace.FlushAll(TimeSpan.FromSeconds(2));

            using var service = new NoteSearchService(env.Store, () => SearchSettings.Off);
            using var feeder = new SearchFeeder(env.Workspace, service.Indexer, TimeSpan.FromMilliseconds(1));
            Settle(service.Indexer.WhenIdle());
            Assert.Single(service.Indexer.Keywords.Search("open", 10));

            open.TextProvider = () => "edited words";
            env.Workspace.NotifyChanged(open);
            feeder.FlushPending();
            Settle(service.Indexer.WhenIdle());
            Assert.Empty(service.Indexer.Keywords.Search("open", 10));
            Assert.Single(service.Indexer.Keywords.Search("edited", 10));
        });

        /// <summary>
        /// Waits for the indexer's background worker without blocking calls in a test body
        /// (xUnit1031 warns on Task.Wait/.Result there, and the build must gain no warnings).
        /// </summary>
        private static void Settle(Task task)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(5)) System.Threading.Thread.Sleep(5);
            Assert.True(task.IsCompleted, "the indexer did not settle");
        }

        private static SearchSettingsHost Host(AppConfig config, SecretStore secrets) => new()
        {
            Config = config,
            Save = () => { },
            Secrets = secrets,
        };

        [Fact]
        public void The_panel_saves_servers_models_and_keys_where_they_belong() => UiThread.Run(() =>
        {
            using var dir = new PadTempDir();
            var secrets = new SecretStore(Path.Combine(dir.Root, "secrets.bin"));
            var config = new AppConfig();
            var panel = new SearchSettingsPanel();
            panel.Load(Host(config, secrets));

            panel.MeaningToggle.IsOn = true;
            panel.EmbeddingServerBox.Text = "http://gpu:8000/v1/";
            panel.EmbeddingServerBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            panel.EmbeddingModelBox.Text = " bge-m3 ";
            panel.EmbeddingModelBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            panel.EmbeddingKeyBox.Password = "sk-secret";
            panel.SaveEmbeddingKey();

            Assert.True(config.PadSemanticSearch);
            Assert.Equal("http://gpu:8000/v1", config.PadEmbeddingServer);
            Assert.Equal("http://gpu:8000/v1", panel.EmbeddingServerBox.Text);
            Assert.Equal("bge-m3", config.PadEmbeddingModel);
            Assert.Equal("sk-secret", secrets.Get(SecretNames.PadEmbeddingKey));
            Assert.Equal("", panel.EmbeddingKeyBox.Password);
            Assert.Equal(Visibility.Visible, panel.EmbeddingKeySaved.Visibility);
        });

        [Fact]
        public void The_panel_refuses_a_bad_server_and_keeps_the_one_in_use() => UiThread.Run(() =>
        {
            using var dir = new PadTempDir();
            var config = new AppConfig { PadEmbeddingServer = "http://gpu/v1" };
            var panel = new SearchSettingsPanel();
            panel.Load(Host(config, new SecretStore(Path.Combine(dir.Root, "secrets.bin"))));

            panel.EmbeddingServerBox.Text = "ftp://nope";
            panel.EmbeddingServerBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));

            Assert.Equal("http://gpu/v1", config.PadEmbeddingServer);
            Assert.Equal("http://gpu/v1", panel.EmbeddingServerBox.Text);
            Assert.StartsWith("Not a server address", panel.ServerHint.Text, StringComparison.Ordinal);
        });

        [Fact]
        public void Rerank_is_only_offered_with_meaning_search() => UiThread.Run(() =>
        {
            using var dir = new PadTempDir();
            var config = new AppConfig();
            var panel = new SearchSettingsPanel();
            panel.Load(Host(config, new SecretStore(Path.Combine(dir.Root, "secrets.bin"))));

            Assert.False(panel.RerankToggle.IsEnabled);
            panel.MeaningToggle.IsOn = true;
            Assert.True(panel.RerankToggle.IsEnabled);
        });
    }
}
