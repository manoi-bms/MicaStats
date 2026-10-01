using System;
using System.IO;
using Kil0bitSystemMonitor.Services.Pad;

namespace Kil0bitSystemMonitor.Pad
{
    /// <summary>
    /// MicaPad's side of diagram pictures (spec Part 3): the renderer the app shares between
    /// windows, the editor's <see cref="DiagramServices"/>, the clipboard and the Save dialog.
    /// Settings changes reach the editor through <see cref="OnConfigChanged"/>.
    /// </summary>
    public partial class MicaPadWindow
    {
        /// <summary>Draws the diagrams of every MicaPad window; set by the app before the first window opens. Null (most tests) means no pictures.</summary>
        internal static IDiagramRenderer? DiagramRenderer { get; set; }

        /// <summary>Loads the images of every MicaPad window; set by the app before the first window opens. Null (most tests) means no image previews.</summary>
        internal static ImageSources? ImageLoader { get; set; }

        /// <summary>Puts a PNG on the clipboard; false when it stays busy. Tests replace it.</summary>
        internal Func<byte[], bool> TrySetClipboardImage { get; set; } = TrySetClipboardPng;

        /// <summary>Asks where to save a picture (default file name, filter); null when cancelled. Tests replace it.</summary>
        internal Func<string, string, string?> AskSavePath { get; set; } = (_, _) => null;

        /// <summary>Gives the note editor its pictures and image previews; the history preview gets none.</summary>
        private void ConfigureDiagrams()
        {
            AskSavePath = ShowSaveDialog;
            if (ImageLoader is { } images)
            {
                _language.Images = new ImageServices
                {
                    Sources = images,
                    Renderer = DiagramRenderer,
                    Enabled = () => _config.PadDiagrams,
                    WebImages = () => _config.PadWebImages,
                    BaseFolder = FolderOfShown,
                    Warn = message => Warn(message),
                };
            }
            if (DiagramRenderer is not { } renderer) return;
            _language.Diagrams = new DiagramServices
            {
                Renderer = renderer,
                Enabled = () => _config.PadDiagrams,
                KrokiServer = () => _config.PadKroki ? _config.PadKrokiServer : null,
                OpenLink = OnLinkRequested,
                TrySetClipboardImage = png => TrySetClipboardImage(png),
                AskSavePath = (name, filter) => AskSavePath(name, filter),
                ShowStatus = ShowStatus,
                Warn = message => Warn(message),
            };
        }

        /// <summary>Settings → MicaPad changed Draw diagrams, Kroki, the Kroki server or Load images from the web.</summary>
        private void ApplyDiagramSettings() => _language.RefreshDiagrams();

        /// <summary>The folder of the shown tab's file, where relative image paths start; null for a note (R12).</summary>
        private string? FolderOfShown() => _shown?.Meta.SourcePath is string path ? Path.GetDirectoryName(path) : null;

        private string? ShowSaveDialog(string name, string filter)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = name,
                Filter = filter,
                AddExtension = true,
                DefaultExt = Path.GetExtension(name),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }

        /// <summary>
        /// The PNG as "PNG" (with its transparency; Word and browsers take it) and as a bitmap on
        /// white for everything else. The WinForms call tries three times, 100 ms apart, as Copy as
        /// RTF does.
        /// </summary>
        private static bool TrySetClipboardPng(byte[] png)
        {
            try
            {
                using var stream = new MemoryStream(png);
                using var source = new System.Drawing.Bitmap(stream);
                using var flat = new System.Drawing.Bitmap(source.Width, source.Height);
                using (var graphics = System.Drawing.Graphics.FromImage(flat))
                {
                    graphics.Clear(System.Drawing.Color.White);
                    graphics.DrawImage(source, 0, 0, source.Width, source.Height);
                }
                var data = new System.Windows.Forms.DataObject();
                data.SetData("PNG", false, new MemoryStream(png));
                data.SetData(System.Windows.Forms.DataFormats.Bitmap, true, flat);
                System.Windows.Forms.Clipboard.SetDataObject(data, copy: true, retryTimes: 3, retryDelay: 100);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }
        }
    }
}
