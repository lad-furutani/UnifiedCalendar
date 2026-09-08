global using static UnifiedCalendar.Tests.WpfTestDispatcher;

using System.Windows.Threading;
using Xunit;

namespace UnifiedCalendar.Tests;

internal static class WpfTestDispatcher
{
    internal static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    internal static void PumpDispatcherUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 1_000 && !condition(); attempt++)
        {
            PumpDispatcher();
            Thread.Sleep(1);
        }

        Assert.True(condition(), "The expected WPF dispatcher state was not reached.");
    }
}
