using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using Kil0bitSystemMonitor.Helpers;
using Kil0bitSystemMonitor.Services.Localization;
using Kil0bitSystemMonitor.ViewModels;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

[Collection("UiTextGlobal")]
public sealed class OtherUiLocalizationTests
{
    [Fact]
    public void Process_sort_key_survives_Thai_header_and_column_reordering()
    {
        UiThread.Run(() =>
        {
            string previous = UiText.Language;
            try
            {
                UiText.SetLanguage("th");
                var memory = new GridViewColumn { Header = UiText.Tr("Memory") };
                ProcessSortKey.SetKey(memory, "Memory");
                var name = new GridViewColumn { Header = UiText.Tr("Name") };
                ProcessSortKey.SetKey(name, "Name");
                var view = new GridView();
                view.Columns.Add(name);
                view.Columns.Add(memory);

                view.Columns.Remove(memory);
                view.Columns.Insert(0, memory);

                Assert.Equal("หน่วยความจำ", memory.Header);
                Assert.Equal("Memory", ProcessSortKey.GetKey(memory));
                Assert.Equal(ProcessSortColumn.Memory,
                    TaskManagerViewModel.ColumnFor(ProcessSortKey.GetKey(memory)));

                var converter = new ProcessSortGlyphConverter();
                Assert.Equal("▲", converter.Convert(
                    new object[] { ProcessSortKey.GetKey(memory), ProcessSortColumn.Memory, false },
                    typeof(string), null!, CultureInfo.InvariantCulture));
                Assert.Equal("เรียงจากน้อยไปมาก", converter.Convert(
                    new object[] { ProcessSortKey.GetKey(memory), ProcessSortColumn.Memory, false, UiText.Language },
                    typeof(string), "Description", CultureInfo.InvariantCulture));

                var description = new TextBlock();
                var binding = new MultiBinding { Converter = converter, ConverterParameter = "Description" };
                binding.Bindings.Add(new System.Windows.Data.Binding { Source = ProcessSortKey.GetKey(memory) });
                binding.Bindings.Add(new System.Windows.Data.Binding { Source = ProcessSortColumn.Memory });
                binding.Bindings.Add(new System.Windows.Data.Binding { Source = false });
                binding.Bindings.Add(new System.Windows.Data.Binding(nameof(UiTextProvider.Language)) { Source = UiText.Provider });
                description.SetBinding(TextBlock.TextProperty, binding);
                Assert.Equal("เรียงจากน้อยไปมาก", description.Text);
                UiText.SetLanguage("en");
                Assert.Equal("Sorted ascending", description.Text);
                Assert.Equal(ProcessSortColumn.Memory,
                    TaskManagerViewModel.ColumnFor(ProcessSortKey.GetKey(memory)));
            }
            finally
            {
                UiText.SetLanguage(previous);
            }
        });
    }
}
