using Avalonia.Controls;
using Fm27Editor.App;

namespace Fm27Editor.App.Tests;

public class MassEditNumberInputTests
{
    [Fact]
    public void Negative_delta_is_accepted_and_switching_back_to_set_restores_legal_bounds()
    {
        AvaloniaTestThread.Run(() =>
        {
            var input = new NumericUpDown { Minimum = 1, Maximum = 20, Value = 10 };
            MassEditNumberInput.Configure(input, true, 1, 20);
            input.Value = -15;
            Assert.Equal(-15m, input.Value);
            MassEditNumberInput.Configure(input, false, 1, 20);
            Assert.Equal(1m, input.Value);
            Assert.Equal(1m, input.Minimum);
            Assert.Equal(20m, input.Maximum);
        });
    }
}
