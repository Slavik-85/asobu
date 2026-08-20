using Avalonia.Controls;
using Avalonia.Input;
using Asobu.App.ViewModels;

namespace Asobu.App.Views;

public partial class PackInstallView : UserControl
{
    public PackInstallView() => InitializeComponent();

    /// <summary>
    /// Closes the sheet when the dark area around it is pressed. Only for a press that landed
    /// there: one inside the card bubbles up here too, and acting on those would dismiss the
    /// sheet the moment anyone tried to type in it.
    ///
    /// Cancel rather than close, because mid-install Cancel means "stop", and the pack being
    /// built should not be abandoned by a stray click outside the card.
    /// </summary>
    private void Scrim_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;

        if (DataContext is PackInstallViewModel { IsWorking: false } vm) vm.CancelCommand.Execute(null);
    }
}
