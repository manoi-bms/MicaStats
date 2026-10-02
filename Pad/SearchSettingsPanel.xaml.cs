using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Kil0bitSystemMonitor.Services.Ai;
using Kil0bitSystemMonitor.Services.Pad;
using Kil0bitSystemMonitor.Services.Pad.Search;

using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// Settings → MicaPad → Search (search spec 2). Every change is written to the config and saved
    /// at once; App reacts to the changed <c>Pad*</c> search properties. Keys go to the secret store.
    /// </summary>
    public partial class SearchSettingsPanel : UserControl
    {
        private const string ServerHelp = "Base addresses like http://gpu:8000/v1. MicaPad adds /embeddings and /rerank. Changing the embedding server or model makes the vectors again.";

        /// <summary>How often the index line is read again while the panel is shown.</summary>
        public static readonly TimeSpan IndexRefreshEvery = TimeSpan.FromSeconds(1);

        private readonly DispatcherTimer _indexRefresh;
        private SearchSettingsHost? _host;
        private bool _loading;
        private int _embeddingTestRun;
        private int _rerankTestRun;

        public SearchSettingsPanel()
        {
            InitializeComponent();
            // The index line follows the indexer while the panel is shown (first indexing, Rebuild,
            // MicaPad opening meanwhile); the timer runs only between Loaded and Unloaded.
            _indexRefresh = new DispatcherTimer { Interval = IndexRefreshEvery };
            _indexRefresh.Tick += (_, _) => RefreshIndex();
            Loaded += (_, _) =>
            {
                RefreshIndex();
                _indexRefresh.Start();
            };
            Unloaded += (_, _) => _indexRefresh.Stop();
        }

        /// <summary>True while the index line is being kept current (tests).</summary>
        internal bool RefreshesIndex => _indexRefresh.IsEnabled;

        public void Load(SearchSettingsHost host)
        {
            _host = host;
            _loading = true;
            try
            {
                var cfg = host.Config;
                MeaningToggle.IsOn = cfg.PadSemanticSearch;
                EmbeddingServerBox.Text = cfg.PadEmbeddingServer;
                EmbeddingModelBox.Text = cfg.PadEmbeddingModel;
                RerankToggle.IsOn = cfg.PadRerank;
                RerankServerBox.Text = cfg.PadRerankServer;
                RerankModelBox.Text = cfg.PadRerankModel;
                ServerHint.Text = ServerHelp;
                RefreshKeys();
                RefreshEnabled();
                RefreshIndex();
            }
            finally
            {
                _loading = false;
            }
        }

        private void Changed()
        {
            _host!.Save();
            _host.Service()?.Indexer.SettingsChanged();
            RefreshEnabled();
            RefreshIndex();
        }

        private void RefreshEnabled() => RerankToggle.IsEnabled = MeaningToggle.IsOn;

        /// <summary>
        /// The index line and Rebuild, from the running search: on load, after changes and every
        /// <see cref="IndexRefreshEvery"/> while shown. Reads the config, never the keys.
        /// </summary>
        public void RefreshIndex()
        {
            if (_host == null) return;
            var service = _host.Service();
            IndexStatus.Text = service == null
                ? "The index is made when MicaPad opens."
                : SearchStatusText.Index(service.Indexer.Progress, PadSearchSettings.From(_host.Config, _ => null));
            RebuildButton.IsEnabled = service != null;
        }

        private void OnMeaningToggled(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            CommitServer(EmbeddingServerBox, () => _host.Config.PadEmbeddingServer, v => _host.Config.PadEmbeddingServer = v);
            _host.Config.PadSemanticSearch = MeaningToggle.IsOn;
            Changed();
            // Turning it off deletes the stored vectors (spec 2). A running search does so itself;
            // before MicaPad has opened this session there is none, so the file goes from here.
            if (!MeaningToggle.IsOn && _host.Service() == null) _host.DeleteStoredVectors();
        }

        private void OnRerankToggled(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            CommitServer(RerankServerBox, () => _host.Config.PadRerankServer, v => _host.Config.PadRerankServer = v);
            _host.Config.PadRerank = RerankToggle.IsOn;
            Changed();
        }

        private void OnEmbeddingServerChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            if (CommitServer(EmbeddingServerBox, () => _host.Config.PadEmbeddingServer, v => _host.Config.PadEmbeddingServer = v)) Changed();
        }

        private void OnRerankServerChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            if (CommitServer(RerankServerBox, () => _host.Config.PadRerankServer, v => _host.Config.PadRerankServer = v)) Changed();
        }

        /// <summary>
        /// Takes a server box into the config: an http(s) address is kept without its trailing slash,
        /// an empty box clears it, anything else is refused and the box shows the server in use again.
        /// True when the server changed.
        /// </summary>
        private bool CommitServer(TextBox box, Func<string> current, Action<string> set)
        {
            string typed = box.Text.Trim();
            if (typed == current()) return false;
            if (typed.Length == 0)
            {
                set("");
                ServerHint.Text = ServerHelp;
                return true;
            }
            if (!KrokiClient.TryParseServer(typed, out var server))
            {
                box.Text = current();
                ServerHint.Text = "Not a server address. Use http:// or https:// and a host name, like http://gpu:8000/v1.";
                return false;
            }
            box.Text = server;
            ServerHint.Text = ServerHelp;
            if (server == current()) return false;
            set(server);
            return true;
        }

        private void OnEmbeddingModelChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            string model = EmbeddingModelBox.Text.Trim();
            EmbeddingModelBox.Text = model;
            if (model == _host.Config.PadEmbeddingModel) return;
            _host.Config.PadEmbeddingModel = model;
            Changed();
        }

        private void OnRerankModelChanged(object sender, RoutedEventArgs e)
        {
            if (_loading || _host == null) return;
            string model = RerankModelBox.Text.Trim();
            RerankModelBox.Text = model;
            if (model == _host.Config.PadRerankModel) return;
            _host.Config.PadRerankModel = model;
            Changed();
        }

        // ---- keys: the same rules as Settings > AI ----

        private void OnSaveEmbeddingKey(object sender, RoutedEventArgs e) => SaveEmbeddingKey();

        private void OnSaveRerankKey(object sender, RoutedEventArgs e) => SaveKey(SecretNames.PadRerankKey, RerankKeyBox);

        /// <summary>The embedding key's Save button (tests call it directly).</summary>
        internal void SaveEmbeddingKey() => SaveKey(SecretNames.PadEmbeddingKey, EmbeddingKeyBox);

        private void OnRemoveEmbeddingKey(object sender, RoutedEventArgs e) => RemoveKey(SecretNames.PadEmbeddingKey);

        private void OnRemoveRerankKey(object sender, RoutedEventArgs e) => RemoveKey(SecretNames.PadRerankKey);

        private void SaveKey(string name, PasswordBox box)
        {
            if (_host == null) return;
            string key = box.Password.Trim();
            if (key.Length == 0)
            {
                box.Clear();
                ShowKeyHint("Paste the key into the box first, then press Save.");
                return;
            }
            try
            {
                _host.Secrets.Set(name, key);
            }
            catch (Exception ex)
            {
                ShowKeyHint("The key could not be stored (" + ex.GetType().Name + "). It is still in the box; press Save to try again.");
                return;
            }
            if (!string.Equals(_host.Secrets.Get(name), key, StringComparison.Ordinal))
            {
                ShowKeyHint("The key could not be stored. It is still in the box; press Save to try again.");
                return;
            }
            box.Clear();
            ShowKeyHint("Saved. The key is stored encrypted for your Windows account and is not shown again.");
            RefreshKeys();
            _host.Service()?.Indexer.SettingsChanged();   // a waiting retry tries the new key now
        }

        private void RemoveKey(string name)
        {
            if (_host == null) return;
            _host.Secrets.Remove(name);
            ShowKeyHint(!_host.Secrets.CanRead() || _host.Secrets.Has(name)
                ? "The key could not be removed. The file that holds it is not available; try again."
                : "Removed.");
            RefreshKeys();
        }

        private void ShowKeyHint(string text)
        {
            KeyHint.Text = text;
            KeyHint.Visibility = Visibility.Visible;
        }

        private void RefreshKeys()
        {
            if (_host == null) return;
            bool embedding = _host.Secrets.Has(SecretNames.PadEmbeddingKey);
            EmbeddingKeyEntry.Visibility = embedding ? Visibility.Collapsed : Visibility.Visible;
            EmbeddingKeySaved.Visibility = embedding ? Visibility.Visible : Visibility.Collapsed;
            bool rerank = _host.Secrets.Has(SecretNames.PadRerankKey);
            RerankKeyEntry.Visibility = rerank ? Visibility.Collapsed : Visibility.Visible;
            RerankKeySaved.Visibility = rerank ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- Test and Rebuild ----

        private async void OnTestEmbedding(object sender, RoutedEventArgs e)
        {
            if (_host == null) return;
            CommitServer(EmbeddingServerBox, () => _host.Config.PadEmbeddingServer, v => _host.Config.PadEmbeddingServer = v);
            var server = PadSearchSettings.From(_host.Config, _host.Secrets.Get).Embedding;
            if (server == null) { EmbeddingTestResult.Text = "Enter the server address first."; return; }

            int run = ++_embeddingTestRun;
            EmbeddingTestResult.Text = "Testing…";
            var result = await _host.Embedder.EmbedAsync(server, new[] { SearchIndexer.KnownGoodText }, TimeSpan.FromSeconds(15), default);
            if (run != _embeddingTestRun) return;   // a newer test of this server took over
            EmbeddingTestResult.Text = result.Vectors != null
                ? "OK: " + result.Vectors[0].Length + " dimensions"
                : "The server " + SearchFailureText.Describe(result.Failure, result.Status) + ".";
        }

        private async void OnTestRerank(object sender, RoutedEventArgs e)
        {
            if (_host == null) return;
            CommitServer(RerankServerBox, () => _host.Config.PadRerankServer, v => _host.Config.PadRerankServer = v);
            var server = PadSearchSettings.From(_host.Config, _host.Secrets.Get).Reranker;
            if (server == null) { RerankTestResult.Text = "Enter the server address first."; return; }

            int run = ++_rerankTestRun;
            RerankTestResult.Text = "Testing…";
            var result = await _host.Reranker.RerankAsync(server, "test", new[] { "test", "other" }, 2, TimeSpan.FromSeconds(15), default);
            if (run != _rerankTestRun) return;   // a newer test of this server took over
            RerankTestResult.Text = result.Ranked != null
                ? "OK"
                : "The server " + SearchFailureText.Describe(result.Failure, result.Status) + ".";
        }

        private void OnRebuild(object sender, RoutedEventArgs e)
        {
            _host?.Service()?.Indexer.Rebuild();
            IndexStatus.Text = "Rebuilding…";
        }
    }
}
