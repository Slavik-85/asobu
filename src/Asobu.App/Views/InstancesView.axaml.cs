using System.Collections.Generic;
using Asobu.App.ViewModels;
using Asobu.Core.Instances;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Asobu.App.Views;

public partial class InstancesView : UserControl
{
    public InstancesView() => InitializeComponent();

    private void InstanceCard_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Instance instance } && DataContext is InstancesViewModel vm)
            vm.OpenInstanceCommand.Execute(instance);
    }

    private void IconChoice_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string icon } && DataContext is InstancesViewModel vm)
            vm.SelectIconCommand.Execute(icon);
    }

    private async void ExportButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesViewModel { Selected: { } instance } vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export instance",
            SuggestedFileName = instance.Name,
            DefaultExtension = "zip",
            FileTypeChoices = new List<FilePickerFileType>
            {
                new("Asobu instance") { Patterns = ["*.zip"] },
            },
        });

        if (file is null) return;
        await vm.ExportAsync(file.Path.LocalPath);
    }

    private async void ImportButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not InstancesViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import instance",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Asobu instance") { Patterns = ["*.zip"] },
            },
        });

        if (files.Count == 0) return;
        await vm.ImportAsync(files[0].Path.LocalPath);
    }
}
