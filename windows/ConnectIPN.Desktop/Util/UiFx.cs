using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ConnectIPN.Util;

/// <summary>
/// Small shared motion vocabulary: every window opens the same way (rise + fade,
/// decelerated), so switching between them feels like one product.
/// </summary>
internal static class UiFx
{
    public static void SlideIn(UIElement element, int delayMs)
    {
        var translate = new TranslateTransform(0, 18);
        if (element is FrameworkElement { RenderTransform: TransformGroup existing })
        {
            existing.Children.Add(translate);
        }
        else
        {
            element.RenderTransform = translate;
        }
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            Duration = TimeSpan.FromMilliseconds(320),
        });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = 18,
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }
}
