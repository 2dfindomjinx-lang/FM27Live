using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Controls.Presenters;
using Avalonia.Media.Imaging;
using Fm27Editor.Core.Settings;

namespace Fm27Editor.App.Tests;

public sealed class ReadabilityTests
{
    [Theory]
    [InlineData(190)]
    [InlineData(250)]
    public void Turkish_attributes_wrap_without_overlapping_larger_edit_boxes(int width)
    {
        AvaloniaTestThread.Run(() =>
        {
            var previous = LanguageSetting.Current;
            Window? window = null;
            try
            {
                LanguageSetting.Set(AppLanguage.Turkish);
                var fields = PlayerFields.All.Where(f => f.Group == "Technical").ToArray();
                int commits = 0;
                var rows = fields.Select(f => MainWindow.AttributeRow(Loc.Attribute(f.Label), MainWindow.RatingBox(15,1,20,_ => commits++))).ToArray();
                var card = MainWindow.AttributeCard("Technical",rows,rowIds:fields.Select(f=>f.Id).ToArray());
                window = new Window { Content = card, Width = width, Height = 800, ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-10000,-10000), SystemDecorations = SystemDecorations.None };
                window.Show();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                card.Measure(new Size(width, double.PositiveInfinity));
                card.Arrange(new Rect(0,0,width,card.DesiredSize.Height));
                card.UpdateLayout();
                foreach(var row in rows.Cast<DockPanel>())
                {
                    var input = row.Children.OfType<TextBox>().Single();
                    var label = row.Children.OfType<TextBlock>().Single();
                    Assert.True(label.Bounds.Right <= input.Bounds.Left + .1, label.Text);
                    Assert.True(input.Bounds.Width >= 36);
                    Assert.True(input.Bounds.Height >= 26);
                    var presenter = input.GetVisualDescendants().OfType<TextPresenter>().Single();
                    Assert.Equal("15", presenter.Text);
                    // Layout rounds to device pixels (15.136 DIP of text occupies a 15 DIP presenter at 100% DPI).
                    Assert.True(presenter.Bounds.Width >= presenter.TextLayout.Width - .5, $"Width {presenter.Bounds.Width}, text {presenter.TextLayout.Width}");
                    Assert.True(presenter.Bounds.Height >= presenter.TextLayout.Height - .5, $"Height {presenter.Bounds.Height}, text {presenter.TextLayout.Height}");
                    Assert.True(label.Bounds.Height >= label.TextLayout.Height - .1, label.Text);
                }
                Assert.Equal(0,commits);
                if(Environment.GetEnvironmentVariable("FM27_READABILITY_RENDER") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    using var bitmap = new RenderTargetBitmap(new PixelSize(width,(int)Math.Ceiling(card.Bounds.Height)), new Vector(96,96));
                    bitmap.Render(card);
                    bitmap.Save(Path.Combine(directory,$"attributes-{width}.png"));
                }
                window.Content = null;
                window.Close();
            }
            finally { window?.Close(); LanguageSetting.Set(previous); }
        });
    }
}
