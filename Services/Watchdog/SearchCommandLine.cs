using System;
using System.Collections.Generic;

namespace Kil0bitSystemMonitor.Services.Watchdog
{
    /// <summary>
    /// Reads a <c>find</c> command line well enough to answer one question: does this scan have
    /// a bottom?
    ///
    /// <para>
    /// Under Git Bash, <c>/</c> is the whole drive and the walk includes <c>/proc</c>, every
    /// mount, and any mapped or dead network path. A scan rooted there with no
    /// <c>-maxdepth</c> can run effectively forever, which is the condition the watchdog
    /// exists to catch. A scan rooted anywhere else, or bounded by a depth, is somebody doing
    /// ordinary work.
    /// </para>
    /// </summary>
    public static class SearchCommandLine
    {
        /// <summary>
        /// Splits a command line on whitespace, treating a double-quoted run as one token.
        ///
        /// <para>
        /// Quote handling is not decoration: the executable itself is
        /// <c>"C:\Program Files\Git\usr\bin\find.exe"</c>, so a naive split puts
        /// <c>Files\Git\usr\bin\find.exe"</c> where the scan root should be and every command
        /// line on the target machine parses wrongly.
        /// </para>
        /// </summary>
        public static IReadOnlyList<string> Tokenize(string commandLine)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(commandLine)) return tokens;

            var current = new System.Text.StringBuilder();
            bool quoted = false;

            foreach (char c in commandLine)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                    continue;
                }

                if (!quoted && char.IsWhiteSpace(c))
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0) tokens.Add(current.ToString());
            return tokens;
        }

        /// <summary>
        /// The directory the scan starts from, or null when the command line names none.
        ///
        /// <para>
        /// <c>find</c> takes its options in the order <c>find [-H] [-L] [-P] [path...]
        /// [expression]</c>, so the root is the first argument after the executable that is not
        /// one of those three link-handling flags.
        /// </para>
        /// </summary>
        public static string? ScanRoot(string commandLine)
        {
            var tokens = Tokenize(commandLine);

            for (int i = 1; i < tokens.Count; i++)   // index 0 is the executable
            {
                string token = tokens[i];
                if (token.Length == 0) continue;

                if (string.Equals(token, "-H", StringComparison.Ordinal) ||
                    string.Equals(token, "-L", StringComparison.Ordinal) ||
                    string.Equals(token, "-P", StringComparison.Ordinal))
                    continue;

                // Anything else starting with '-' is already the expression, which means no
                // path was given and find defaults to the working directory.
                return token[0] == '-' ? null : token;
            }

            return null;
        }

        /// <summary>Whether the command line bounds the walk with <c>-maxdepth</c> anywhere.</summary>
        public static bool HasMaxDepth(string commandLine)
        {
            foreach (string token in Tokenize(commandLine))
            {
                if (string.Equals(token, "-maxdepth", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// Whether an argument names a whole filesystem rather than a subtree.
        ///
        /// <para>
        /// Four spellings reach the same place on this platform: the POSIX root <c>/</c>, a
        /// Windows drive root in either slash (<c>C:\</c>, <c>C:/</c>), a bare drive
        /// (<c>C:</c>), and the MSYS mount form (<c>/c/</c>). All four are unbounded; anything
        /// with a component below them is not.
        /// </para>
        /// </summary>
        public static bool IsFilesystemRoot(string argument)
        {
            if (string.IsNullOrEmpty(argument)) return false;

            string path = argument.Replace('\\', '/');

            if (path == "/") return true;

            // C:  C:/  c:\ -> "c:" or "c:/"
            if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
                return path.Length == 2 || (path.Length == 3 && path[2] == '/');

            // /c/ and /c
            if (path.Length >= 2 && path[0] == '/' && char.IsLetter(path[1]))
                return path.Length == 2 || (path.Length == 3 && path[2] == '/');

            return false;
        }

        /// <summary>
        /// Whether this command line requests a scan with no bottom: rooted at a whole
        /// filesystem and not bounded by a depth.
        /// </summary>
        public static bool IsUnbounded(string commandLine)
        {
            string? root = ScanRoot(commandLine);
            return root != null && IsFilesystemRoot(root) && !HasMaxDepth(commandLine);
        }
    }
}
