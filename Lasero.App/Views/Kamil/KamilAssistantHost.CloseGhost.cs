using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Lasero.App.Controls.Motion;

namespace Lasero.App.Views.Kamil;

/// <summary>
/// Minimizing into the avatar. Shrinking the live panel left its fixed-size content overflowing the
/// shrinking card (it read as a flicker). Instead the panel is captured as a picture, the real UI snaps
/// to the minimized state at once, and the picture scales down and fades into the avatar.
/// </summary>
public partial class KamilAssistantHost
{
    private sealed record CloseGhost(BitmapSource Picture, Point Position, Size Size);

    private CloseGhost? CaptureCloseGhost()
    {
        if (Surface.ActualWidth < 24 || Surface.ActualHeight < 24) return null;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(Surface);
            var width = (int)Math.Ceiling(Surface.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Ceiling(Surface.ActualHeight * dpi.DpiScaleY);
            var picture = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            picture.Render(Surface);
            picture.Freeze();
            return new CloseGhost(picture, CurrentSurfacePosition(), new Size(Surface.ActualWidth, Surface.ActualHeight));
        }
        catch (Exception)
        {
            return null; // The snapshot is decoration; without it the panel simply snaps shut.
        }
    }

    private void PlayCloseGhost(CloseGhost ghost, Point avatarOrigin)
    {
        if (!LaseroMotion.AnimationsEnabled) return;

        var target = new Point(avatarOrigin.X + PillWidth / 2, avatarOrigin.Y + PillHeight / 2);
        var originX = Math.Clamp((target.X - ghost.Position.X) / ghost.Size.Width, 0, 1);
        var originY = Math.Clamp((target.Y - ghost.Position.Y) / ghost.Size.Height, 0, 1);

        var scale = new ScaleTransform(1, 1);
        var image = new Image
        {
            Source = ghost.Picture,
            Width = ghost.Size.Width,
            Height = ghost.Size.Height,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(originX, originY),
            RenderTransform = scale,
        };
        Canvas.SetLeft(image, ghost.Position.X);
        Canvas.SetTop(image, ghost.Position.Y);
        Root.Children.Add(image);

        var duration = TimeSpan.FromMilliseconds(260);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => Root.Children.Remove(image);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 0.12, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.12, duration) { EasingFunction = ease });
        image.BeginAnimation(OpacityProperty, fade);
    }
}
