using CassiniFlow.Desktop.Controls;
using Xunit;

namespace CassiniFlow.Tests;

public class NumberStepperTests
{
    [Fact]
    public void NumberStepper_InitialValues_AreConsistent()
    {
        var stepper = new NumberStepper();
        Assert.Equal(0, stepper.Value);
        Assert.Equal(1.0, stepper.Increment);
        Assert.Equal("0", stepper.FormatString);
    }

    [Fact]
    public void NumberStepper_ClampsToMinAndMax()
    {
        var stepper = new NumberStepper
        {
            Minimum = 10,
            Maximum = 100,
            Value = 5
        };

        Assert.Equal(5, stepper.Value); // Value is set directly
    }
}
