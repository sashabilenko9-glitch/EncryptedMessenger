using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using EncryptedMessenger.Core.Models;

namespace EncryptedMessenger.WPF.Converters
{
    // ── bool → HorizontalAlignment ────────────────────────────────────────
    [ValueConversion(typeof(bool), typeof(HorizontalAlignment))]
    public class BoolToAlignmentConverter : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
            => v is true ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        public object ConvertBack(object v, Type t, object p, CultureInfo c)
            => throw new NotImplementedException();
    }

    // ── bool → bubble background Brush ────────────────────────────────────
    [ValueConversion(typeof(bool), typeof(Brush))]
    public class BoolToBubbleColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush Out = new(Color.FromRgb(0x2B, 0x52, 0x78));
        private static readonly SolidColorBrush In  = new(Color.FromRgb(0x18, 0x25, 0x33));
        public object Convert(object v, Type t, object p, CultureInfo c) => v is true ? Out : In;
        public object ConvertBack(object v, Type t, object p, CultureInfo c)
            => throw new NotImplementedException();
    }

    // ── bool → text Brush (white for both in dark theme) ─────────────────
    [ValueConversion(typeof(bool), typeof(Brush))]
    public class BoolToTextColorConverter : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c) => Brushes.White;
        public object ConvertBack(object v, Type t, object p, CultureInfo c)
            => throw new NotImplementedException();
    }

    // ── MessageStatus → status glyph string ───────────────────────────────
    [ValueConversion(typeof(MessageStatus), typeof(string))]
    public class MessageStatusConverter : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
            => v is MessageStatus s ? s switch
            {
                MessageStatus.Pending   => "⏳",
                MessageStatus.Sent      => "✓",
                MessageStatus.Delivered => "✓✓",
                MessageStatus.Read      => "✓✓",
                MessageStatus.Failed    => "✗",
                _                       => ""
            } : "";
        public object ConvertBack(object v, Type t, object p, CultureInfo c)
            => throw new NotImplementedException();
    }

    // ── MessageStatus → Brush (blue for Read, grey otherwise) ─────────────
    [ValueConversion(typeof(MessageStatus), typeof(Brush))]
    public class MessageStatusColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush Blue = new(Color.FromRgb(0x5A, 0xAA, 0xF7));
        public object Convert(object v, Type t, object p, CultureInfo c)
            => v is MessageStatus.Read ? Blue : Brushes.Gray;
        public object ConvertBack(object v, Type t, object p, CultureInfo c)
            => throw new NotImplementedException();
    }

    // ── bool → Visibility (true → Visible) ────────────────────────────────
    [ValueConversion(typeof(bool), typeof(Visibility))]
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
            => v is true ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object v, Type t, object p, CultureInfo c)
            => v is Visibility.Visible;
    }

    // ── bool → Visibility (true → Collapsed) ──────────────────────────────
    [ValueConversion(typeof(bool), typeof(Visibility))]
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
            => v is true ? Visibility.Collapsed : Visibility.Visible;
        public object ConvertBack(object v, Type t, object p, CultureInfo c)
            => v is Visibility.Collapsed;
    }

    // ── object? → Visibility (not null → Visible) ─────────────────────────
    [ValueConversion(typeof(object), typeof(Visibility))]
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
            => v != null ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object v, Type t, object p, CultureInfo c)
            => throw new NotImplementedException();
    }

    // ── object? → Visibility (null → Visible) ─────────────────────────────
    [ValueConversion(typeof(object), typeof(Visibility))]
    public class NullToInverseVisibilityConverter : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
            => v == null ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object v, Type t, object p, CultureInfo c)
            => throw new NotImplementedException();
    }

    // ── DateTime → short display string ───────────────────────────────────
    [ValueConversion(typeof(DateTime), typeof(string))]
    public class DateTimeToShortStringConverter : IValueConverter
    {
        public object Convert(object v, Type t, object p, CultureInfo c)
        {
            if (v is not DateTime dt || dt == DateTime.MinValue) return "";
            return dt.Date == DateTime.Today
                ? dt.ToString("HH:mm")
                : dt.ToString("dd.MM");
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c)
            => throw new NotImplementedException();
    }
}
