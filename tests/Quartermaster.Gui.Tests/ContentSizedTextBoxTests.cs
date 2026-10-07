using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Quartermaster.Gui.Shared;
using Xunit;

namespace Quartermaster.Gui.Tests;

public sealed class ContentSizedTextBoxTests
{
    [AvaloniaFact]
    public void FieldsGrowShrinkAndLeaveSpaceForButtonsWhenTheWindowNarrows()
    {
        var input = new ContentSizedTextBox { Text = "short" };
        var button = new Button { Content = "Browse" };
        Grid.SetColumn(button, 1);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(input); row.Children.Add(button);
        var window = new Window { Width = 700, Height = 100, Content = row };
        window.Show();
        try
        {
            void Layout() { Dispatcher.UIThread.RunJobs(); window.CaptureRenderedFrame()?.Dispose(); }
            Layout(); var shortWidth = input.Bounds.Width;
            input.Text = new string('W', 35); Layout();
            Assert.True(input.Bounds.Width > shortWidth);
            input.Text = new string('W', 400); Layout();
            Assert.True(input.Bounds.Right <= button.Bounds.Left);
            Assert.True(input.Bounds.Width <= input.MaxWidth);
            window.Width = 320; Layout();
            Assert.True(input.Bounds.Width < shortWidth + 50);
            Assert.True(input.Bounds.Right <= button.Bounds.Left);
            Assert.True(button.Bounds.Right <= row.Bounds.Width);
            window.Width = 700; input.Text = "short"; Layout();
            Assert.Equal(shortWidth, input.Bounds.Width);
            input.PasswordChar = '*'; input.Text = new string('i', 50); Layout();
            var maskedWidth = input.Bounds.Width;
            input.Text = new string('W', 50); Layout();
            Assert.Equal(maskedWidth, input.Bounds.Width);
        }
        finally { window.Close(); }
    }
}
