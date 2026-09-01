using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyCalendarWidget.Views
{
    public class VersionItemModel
    {
        public Version Version { get; set; }
        public string VersionTitle { get; set; }
        public string ReleaseNotes { get; set; }
        public string DownloadUrl { get; set; }
        public string ButtonText { get; set; } = "Cài đặt";
        public bool IsInstallEnabled { get; set; } = true;
        public double ButtonOpacity { get; set; } = 1.0;
        public Brush ButtonForeground { get; set; } = (Brush)new BrushConverter().ConvertFromString("#8AB4F8");
    }

    public partial class UpdateWindow : Window
    {
        private VersionItemModel _latestItem;
        private string _pendingDownloadUrl = string.Empty;

        public UpdateWindow(List<VersionItemModel> versions, Version currentAppVersion)
        {
            InitializeComponent();

            if (versions != null && versions.Count > 0)
            {
                // Chuẩn hóa so sánh phiên bản hiện tại (chỉ so khớp Major.Minor.Build)
                Version normalizedCurrent = new Version(currentAppVersion.Major, currentAppVersion.Minor, Math.Max(0, currentAppVersion.Build));

                foreach (var v in versions)
                {
                    Version normalizedV = new Version(v.Version.Major, v.Version.Minor, Math.Max(0, v.Version.Build));
                    if (normalizedV == normalizedCurrent)
                    {
                        v.VersionTitle = $"v{v.Version.Major}.{v.Version.Minor}.{v.Version.Build} (Bản hiện tại)";
                        v.ButtonText = "Đang dùng";
                        v.IsInstallEnabled = false;
                        v.ButtonOpacity = 0.4;
                        v.ButtonForeground = Brushes.Gray;
                    }
                }

                // 1. Cấu hình hiển thị phiên bản mới nhất (Khối trên cùng)
                _latestItem = versions[0];
                Version normalizedLatest = new Version(_latestItem.Version.Major, _latestItem.Version.Minor, Math.Max(0, _latestItem.Version.Build));
                bool isLatestCurrent = (normalizedLatest == normalizedCurrent);

                txtLatestVerTitle.Text = $"Phiên bản {_latestItem.VersionTitle}";
                txtLatestNotes.Text = string.IsNullOrWhiteSpace(_latestItem.ReleaseNotes)
                    ? "Bản cập nhật tối ưu hóa và sửa lỗi hệ thống."
                    : _latestItem.ReleaseNotes;

                if (isLatestCurrent)
                {
                    txtLatestBadge.Text = "ĐANG DÙNG";
                    txtLatestBadge.Foreground = Brushes.Gray;
                    brdBadgeLatest.Background = (Brush)new BrushConverter().ConvertFromString("#33888888");

                    btnDownloadLatest.Content = "Đang sử dụng";
                    btnDownloadLatest.IsEnabled = false;
                    btnDownloadLatest.Opacity = 0.4;
                    btnDownloadLatest.Background = Brushes.Gray;
                }
                else
                {
                    txtLatestBadge.Text = "MỚI NHẤT";
                    txtLatestBadge.Foreground = (Brush)new BrushConverter().ConvertFromString("#34C759");
                    brdBadgeLatest.Background = (Brush)new BrushConverter().ConvertFromString("#4434C759");

                    btnDownloadLatest.Content = "⚡ Cập nhật";
                    btnDownloadLatest.IsEnabled = true;
                    btnDownloadLatest.Opacity = 1.0;
                    btnDownloadLatest.Background = Brushes.Cyan;
                }

                // 2. Cấu hình danh sách các phiên bản cũ hơn
                if (versions.Count > 1)
                {
                    panelOlderHeader.Visibility = Visibility.Visible;
                    var olderList = versions.GetRange(1, versions.Count - 1);

                    foreach (var item in olderList)
                    {
                        Version normalizedItem = new Version(item.Version.Major, item.Version.Minor, Math.Max(0, item.Version.Build));
                        if (normalizedItem == normalizedCurrent)
                        {
                            item.VersionTitle = $"Phiên bản hiện tại v{item.Version.Major}.{item.Version.Minor}.{item.Version.Build}";
                            item.ButtonText = "Đang dùng";
                            item.IsInstallEnabled = false;
                            item.ButtonOpacity = 0.4;
                            item.ButtonForeground = Brushes.Gray;
                        }
                    }

                    itemsOlderVersions.ItemsSource = olderList;
                }
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void BtnDownloadLatest_Click(object sender, RoutedEventArgs e)
        {
            if (_latestItem != null)
            {
                _pendingDownloadUrl = _latestItem.DownloadUrl;
                overlayConfirm.Visibility = Visibility.Visible;
            }
        }

        private void BtnDownloadSpecific_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag != null)
            {
                _pendingDownloadUrl = btn.Tag.ToString();
                overlayConfirm.Visibility = Visibility.Visible;
            }
        }

        private void BtnCancelConfirm_Click(object sender, RoutedEventArgs e)
        {
            overlayConfirm.Visibility = Visibility.Collapsed;
            _pendingDownloadUrl = string.Empty;
        }

        private async void BtnAcceptConfirm_Click(object sender, RoutedEventArgs e)
        {
            overlayConfirm.Visibility = Visibility.Collapsed;
            if (!string.IsNullOrEmpty(_pendingDownloadUrl))
            {
                await StartDownloadAndInstallAsync(_pendingDownloadUrl);
            }
        }

        private async Task StartDownloadAndInstallAsync(string downloadUrl)
        {
            panelDownloading.Visibility = Visibility.Visible;
            string tempMsiPath = Path.Combine(Path.GetTempPath(), $"MyCalendarWidgetSetup_{Guid.NewGuid().ToString().Substring(0, 5)}.msi");

            bool success = await DownloadInstallerWithProgressAsync(downloadUrl, tempMsiPath);

            if (success && File.Exists(tempMsiPath))
            {
                txtDownloadStatus.Text = "Đang khởi chạy bộ cài đặt...";
                await Task.Delay(500);

                try
                {
                    // 🛡️ DÙNG TRỰC TIẾP ĐƯỜNG DẪN FILE MSI ĐỂ WINDOWS TỰ GỌI TRÌNH INSTALLER AN TOÀN
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = tempMsiPath,
                        UseShellExecute = true
                    });

                    // 🛑 TẮT HẲN ỨNG DỤNG CŨ ĐỂ NHƯỜNG QUYỀN GHI ĐÈ CHO BỘ CÀI
                    Application.Current.Shutdown();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Không thể khởi chạy bộ cài đặt: " + ex.Message, "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                    panelDownloading.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                MessageBox.Show("Tải tệp cài đặt thất bại. Vui lòng kiểm tra lại kết nối mạng!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                panelDownloading.Visibility = Visibility.Collapsed;
            }
        }

        private async Task<bool> DownloadInstallerWithProgressAsync(string url, string destinationPath)
        {
            try
            {
                var handler = new HttpClientHandler { AllowAutoRedirect = true };
                using (var client = new HttpClient(handler))
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                    var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                    // Xử lý xác nhận vượt qua cảnh báo tệp lớn của Google Drive
                    if (response.Content.Headers.ContentType?.MediaType == "text/html")
                    {
                        string htmlContent = await response.Content.ReadAsStringAsync();
                        if (htmlContent.Contains("confirm="))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(htmlContent, @"confirm=([^&""]+)");
                            if (match.Success)
                            {
                                string confirmCode = match.Groups[1].Value;
                                url += $"&confirm={confirmCode}";
                                response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                            }
                        }
                    }

                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;

                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var buffer = new byte[8192];
                        long totalRead = 0;
                        int read;

                        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, read);
                            totalRead += read;

                            if (totalBytes > 0)
                            {
                                int progressPercent = (int)((totalRead * 100) / totalBytes);
                                pbDownload.Value = progressPercent;
                                txtProgressPercent.Text = $"{progressPercent}%";
                            }
                        }
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}