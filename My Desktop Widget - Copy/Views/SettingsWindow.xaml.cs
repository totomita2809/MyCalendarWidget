using System;
using System.Windows;

namespace MyCalendarWidget.Views
{
    public partial class SettingsWindow : Window
    {
        // Biến callback để báo về MainWindow
        public Action<double> OnOpacityChanged { get; set; }

        // Cờ kiểm tra: Chỉ cho phép hoạt động khi cửa sổ đã nạp xong
        private bool _isFullyLoaded = false;

        public SettingsWindow()
        {
            InitializeComponent();
            _isFullyLoaded = false;

            this.Loaded += (s, e) =>
            {
                // Delay một chút để đảm bảo giá trị từ MainWindow đã được nạp vào Slider
                _isFullyLoaded = true;
                System.Diagnostics.Debug.WriteLine($"DEBUG Settings: Cửa sổ cài đặt đã sẵn sàng. Giá trị Slider hiện tại: {OpacitySlider.Value}");
            };
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // LỚP CHẶN 1: Nếu cửa sổ chưa Loaded xong thì tuyệt đối không xử lý
            if (!_isFullyLoaded) return;

            // LỚP CHẶN 2: Nếu giá trị nhảy về 0 một cách bất thường (do reset) thì bỏ qua
            if (e.NewValue <= 0.01) return;

            // 1. Thực hiện thay đổi qua Callback
            OnOpacityChanged?.Invoke(e.NewValue);

            // 2. Cập nhật trực tiếp lên MainWindow để đảm bảo tính đồng bộ
            if (Application.Current.MainWindow is MainWindow mainWin)
            {
                if (mainWin.MainPanel != null && mainWin.MainPanel.Background != null)
                {
                    mainWin.MainPanel.Background.Opacity = e.NewValue;
                }

                // 3. LƯU VÀO SETTINGS: Chỉ lưu khi giá trị thực sự thay đổi từ người dùng
                Properties.Settings.Default.WidgetOpacity = e.NewValue;
                Properties.Settings.Default.Save();

                System.Diagnostics.Debug.WriteLine($"DEBUG Settings: Đã lưu độ mờ mới = {e.NewValue:F2}");
            }
        }

        // Đảm bảo nút đóng hoạt động để không bị lỗi biên dịch
        private void MenuClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}