using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Quartermaster.Gui;

public partial class MainWindow
{
    private PageKind? positionedTourPage;
    private int pointerDirection = -1;

    private void ConfigureOnboarding()
    {
        // Reposition after navigation, scrolling, text layout, and window resizing.
        LayoutUpdated += (_, _) => PositionOnboardingCard();
    }

    private void PositionOnboardingCard()
    {
        if (DataContext is not MainWindowViewModel model || !model.Onboarding.IsTourVisible)
        {
            positionedTourPage = null;
            return;
        }
        var buttons = this.GetVisualDescendants().OfType<Button>();
        var target = model.Onboarding.TourPage switch
        {
            PageKind.Mods => buttons.FirstOrDefault(button => button.Name == "AddModButton"),
            PageKind.Profiles => AddProfileButton,
            _ => null
        };
        target ??= buttons.FirstOrDefault(button => button.DataContext is NavigationItem { IsTourTarget: true });
        if (target is null || !target.IsEffectivelyVisible) return;
        if (positionedTourPage != model.Onboarding.TourPage)
        {
            positionedTourPage = model.Onboarding.TourPage;
            target.BringIntoView();
        }
        var origin = target.TranslatePoint(default, OnboardingLayer);
        if (origin is null || OnboardingTour.Bounds.Width <= 0 || OnboardingTour.Bounds.Height <= 0) return;
        var size = OnboardingTour.Bounds.Size;
        var viewport = OnboardingLayer.Bounds.Size;
        const double gap = 12;
        const double inset = 12;
        var x = origin.Value.X + target.Bounds.Width + gap;
        var y = origin.Value.Y + (target.Bounds.Height - size.Height) / 2;
        if (model.Onboarding.TourPage == PageKind.Mods)
        {
            // The library's Add mod button sits along the top, so place its card below it.
            x = origin.Value.X + target.Bounds.Width - size.Width;
            y = origin.Value.Y + target.Bounds.Height + gap;
        }
        else if (x + size.Width > viewport.Width - inset)
            x = origin.Value.X - size.Width - gap;
        x = Math.Clamp(x, inset, Math.Max(inset, viewport.Width - size.Width - inset));
        y = Math.Clamp(y, inset, Math.Max(inset, viewport.Height - size.Height - inset));
        if (Canvas.GetLeft(OnboardingTour) != x) Canvas.SetLeft(OnboardingTour, x);
        if (Canvas.GetTop(OnboardingTour) != y) Canvas.SetTop(OnboardingTour, y);
        double pointerX, pointerY;
        var direction = model.Onboarding.TourPage == PageKind.Mods ? 0 : x >= origin.Value.X + target.Bounds.Width ? 1 : 2;
        if (pointerDirection != direction)
        {
            pointerDirection = direction;
            OnboardingPointer.Points = direction switch
            {
                0 => [new(0, 12), new(8, 0), new(16, 12)],
                1 => [new(12, 0), new(0, 8), new(12, 16)],
                _ => [new(0, 0), new(12, 8), new(0, 16)]
            };
        }
        if (model.Onboarding.TourPage == PageKind.Mods)
        {
            pointerX = Math.Clamp(origin.Value.X + target.Bounds.Width / 2 - 8, x + 12, x + size.Width - 28);
            pointerY = y - 12;
        }
        else
        {
            var onRight = x >= origin.Value.X + target.Bounds.Width;
            pointerX = onRight ? x - 12 : x + size.Width;
            pointerY = Math.Clamp(origin.Value.Y + target.Bounds.Height / 2 - 8, y + 12, y + size.Height - 28);
        }
        if (Canvas.GetLeft(OnboardingPointer) != pointerX) Canvas.SetLeft(OnboardingPointer, pointerX);
        if (Canvas.GetTop(OnboardingPointer) != pointerY) Canvas.SetTop(OnboardingPointer, pointerY);
    }
}
