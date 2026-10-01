using System;
using System.Windows;

namespace Kil0bitSystemMonitor.Ai
{
    /// <summary>
    /// The Ask window's control styles. AskWindow.xaml merges an instance; code that builds the
    /// transcript asks <see cref="Get"/>, which keeps one instance per UI thread (styles belong to
    /// the thread that created them).
    /// </summary>
    public partial class ChatStyles : ResourceDictionary
    {
        [ThreadStatic]
        private static ChatStyles? t_shared;

        /// <summary>Loads the styles.</summary>
        public ChatStyles()
        {
            InitializeComponent();
        }

        /// <summary>The style named <paramref name="key"/>.</summary>
        internal static Style Get(string key) => (Style)(t_shared ??= new ChatStyles())[key];
    }
}
