global using static UnifiedCalendar.Tests.WpfTestDispatcher;

using System.Diagnostics;
using System.Runtime.CompilerServices;
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

    internal static void PumpDispatcherUntil(
        Func<bool> condition,
        [CallerArgumentExpression(nameof(condition))] string? conditionText = null)
    {
        var timeout = TimeSpan.FromSeconds(10);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout && !condition())
        {
            PumpDispatcher();
            Thread.Sleep(1);
        }

        Assert.True(
            condition(),
            $"The expected WPF dispatcher state was not reached within " +
            $"{timeout.TotalSeconds:0} seconds: {conditionText}");
    }
}
