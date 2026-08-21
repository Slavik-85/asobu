using Asobu.App.Controls;
using Avalonia.Controls;

namespace Asobu.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // The title bar belongs to Windows and cannot be styled from XAML, so it is told what
        // colour to be. Nothing happens on Windows 10 or on Linux, where a system-coloured bar
        // is what every other window has.
        TitleBarColour.Follow(this);
    }
}
