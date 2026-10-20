using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Something.App;

public sealed partial class MainWindow
{
    private enum BrowseDirection
    {
        Back,
        Forward,
        Up,
    }

    private bool inputNavigationRunning;

    private void InitializeNavigationInput()
    {
        WindowLayout.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnNavigationPointerPressed),
            handledEventsToo: true
        );
        // WinUI's XAML key-name converter does not recognize GoBack/GoForward.
        (VirtualKey Key, VirtualKeyModifiers Modifiers)[] shortcuts =
        [
            (VirtualKey.GoBack, VirtualKeyModifiers.None),
            (VirtualKey.GoForward, VirtualKeyModifiers.None),
            (VirtualKey.Left, VirtualKeyModifiers.Menu),
            (VirtualKey.Right, VirtualKeyModifiers.Menu),
            (VirtualKey.Up, VirtualKeyModifiers.Menu),
        ];
        foreach (var (key, modifiers) in shortcuts)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += OnNavigationAcceleratorInvoked;
            WindowLayout.KeyboardAccelerators.Add(accelerator);
        }
        // These shortcuts belong to the whole window. Without this, WinUI shows the first key
        // name ("GoBack") as a tooltip wherever the pointer rests on an area without its own tooltip.
        WindowLayout.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    private async void OnNavigationPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        BrowseDirection? direction = args.GetCurrentPoint(
            WindowLayout
        ).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.XButton1Pressed => BrowseDirection.Back,
            PointerUpdateKind.XButton2Pressed => BrowseDirection.Forward,
            _ => null,
        };
        if (direction is null)
            return;
        args.Handled = true;
        await NavigateFromInputAsync(direction.Value);
    }

    private async void OnNavigationAcceleratorInvoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args
    )
    {
        BrowseDirection? direction = sender.Key switch
        {
            VirtualKey.GoBack or VirtualKey.Left => BrowseDirection.Back,
            VirtualKey.GoForward or VirtualKey.Right => BrowseDirection.Forward,
            VirtualKey.Up => BrowseDirection.Up,
            _ => null,
        };
        if (direction is null)
            return;
        args.Handled = true;
        await NavigateFromInputAsync(direction.Value);
    }

    private async Task<bool> NavigateFromInputAsync(BrowseDirection direction)
    {
        if (
            closed
            || starting
            || inputNavigationRunning
            || historyMaintenance
            || changingVisibility
            || viewUpdates.Count != 0
            || ViewLoadingOverlay.Visibility == Visibility.Visible
            || VisualTreeHelper
                .GetOpenPopupsForXamlRoot(WindowLayout.XamlRoot)
                .Any(popup => popup.Child is not Microsoft.UI.Xaml.Controls.ToolTip)
        )
            return false;
        if (SettingsOpen)
        {
            if (direction != BrowseDirection.Back)
                return false;
            SetSettingsOpen(false);
            return true;
        }
        if (ReferenceEquals(WorkspaceTabs.SelectedItem, DatabaseWorkspaceTab))
            return false;
        bool enabled = direction switch
        {
            BrowseDirection.Back => BackButton.IsEnabled,
            BrowseDirection.Forward => ForwardButton.IsEnabled,
            BrowseDirection.Up => UpButton.IsEnabled,
            _ => false,
        };
        if (!enabled)
            return false;
        inputNavigationRunning = true;
        Task? task = null;
        try
        {
            task = direction switch
            {
                BrowseDirection.Back => NavigateBackAsync(),
                BrowseDirection.Forward => NavigateForwardAsync(),
                _ => NavigateUpAsync(),
            };
            historyTasks.Add(task);
            await task;
            return true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error)
        {
            ShowError(error);
            return false;
        }
        finally
        {
            if (task is not null)
                historyTasks.Remove(task);
            inputNavigationRunning = false;
        }
    }
}
