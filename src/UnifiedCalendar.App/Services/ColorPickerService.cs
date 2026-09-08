using DrawingColor = System.Drawing.Color;
using FormsColorDialog = System.Windows.Forms.ColorDialog;
using FormsDialogResult = System.Windows.Forms.DialogResult;
using FormsWindow = System.Windows.Forms.IWin32Window;
using UnifiedCalendar.Core.Models;

namespace UnifiedCalendar.App.Services;

public interface IColorPickerService
{
    RgbColor? PickColor(nint ownerWindowHandle, RgbColor initialColor);
}

public sealed class WindowsColorPickerService : IColorPickerService
{
    public RgbColor? PickColor(nint ownerWindowHandle, RgbColor initialColor)
    {
        if (ownerWindowHandle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerWindowHandle));
        }

        using var dialog = new FormsColorDialog
        {
            Color = DrawingColor.FromArgb(
                initialColor.Red,
                initialColor.Green,
                initialColor.Blue),
            FullOpen = true,
        };
        if (dialog.ShowDialog(new WindowHandle(ownerWindowHandle)) != FormsDialogResult.OK)
        {
            return null;
        }

        return new RgbColor(dialog.Color.R, dialog.Color.G, dialog.Color.B);
    }

    private sealed class WindowHandle(nint handle) : FormsWindow
    {
        public nint Handle { get; } = handle;
    }
}
