using CSharpFFPlayer;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel;

namespace CSharpFFPlayer
{
    public partial class MainWindow : MahApps.Metro.Controls.MetroWindow, INotifyPropertyChanged
    {
        private VideoPlayController? _videoPlayController = null;
        private ImageSource _imageSource;

        private bool isDraggingSlider = false;   // ユーザーがスライダーを操作中かどうか
        private bool isUpdatingSlider = false;   // スライダー更新ループ制御

        // 描画方式（メニューから切り替え可能。既定は D3D 経路）
        private RenderTargetType _renderTargetType = RenderTargetType.D3DImage;

        public event PropertyChangedEventHandler? PropertyChanged;

        private string _currentTimeDisplay = "00:00";
        public string CurrentTimeDisplay
        {
            get => _currentTimeDisplay;
            set
            {
                if (_currentTimeDisplay != value)
                {
                    _currentTimeDisplay = value;
                    OnPropertyChanged(nameof(CurrentTimeDisplay));
                }
            }
        }

        private string _totalDurationDisplay = "00:00";
        public string TotalDurationDisplay
        {
            get => _totalDurationDisplay;
            set
            {
                if (_totalDurationDisplay != value)
                {
                    _totalDurationDisplay = value;
                    OnPropertyChanged(nameof(TotalDurationDisplay));
                }
            }
        }

        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public MainWindow()
        {
            InitializeComponent();
            UpdateRenderTargetMenu();
        }

        /// <summary>
        /// 描画方式メニューのチェック状態を現在の設定に同期する
        /// </summary>
        private void UpdateRenderTargetMenu()
        {
            MenuRenderWriteableBitmap.IsChecked = _renderTargetType == RenderTargetType.WriteableBitmap;
            MenuRenderD3DImage.IsChecked = _renderTargetType == RenderTargetType.D3DImage;
        }

        /// <summary>
        /// メニューから描画方式（通常 / D3D）を切り替える
        /// </summary>
        private async void RenderTarget_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag) return;
            if (!Enum.TryParse<RenderTargetType>(tag, out var target)) return;

            // 選択中の項目を再クリックした場合はチェック状態だけ戻す
            if (target == _renderTargetType)
            {
                UpdateRenderTargetMenu();
                return;
            }

            _renderTargetType = target;
            UpdateRenderTargetMenu();

            // 未読み込み／停止中は次回のファイル読み込み時に反映する
            if (_videoPlayController == null || !_videoPlayController.CanSwitchRenderTarget)
            {
                Console.WriteLine($"[描画切替] 次回のファイル読み込み時に {target} を適用します。");
                return;
            }

            ShowLoading(true);
            try
            {
                await _videoPlayController.SwitchRenderTargetAsync(target, src =>
                {
                    _imageSource = src;
                    VideoImage.Source = src;
                });
                Console.WriteLine($"[描画切替] {target} に切り替えました。");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"描画方式の切り替えに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                Console.WriteLine($"[エラー] 描画切替: {ex}");

                // 実際の状態に合わせてメニュー表示を戻す
                _renderTargetType = _videoPlayController.CurrentRenderTarget;
                UpdateRenderTargetMenu();
            }
            finally
            {
                ShowLoading(false);
            }
        }

        private void ShowLoading(bool isVisible) =>
            LoadingOverlay.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// メニューやボタンから動画ファイルを開く処理
        /// </summary>
        private async void OpenVideo(object sender, RoutedEventArgs e)
        {
            if (!TryOpenVideoFile(out string filePath))
                return;

            ShowLoading(true);
            try
            {
                _videoPlayController?.Stop();

                // ファイル読み込みは別スレッドで実行
                _videoPlayController = await Task.Run(() =>
                {
                    var controller = new VideoPlayController();
                    controller.OpenFile(filePath);
                    Console.WriteLine($"[動画読込] {filePath} を読み込み完了");
                    return controller;
                });

                await InitializeAndStartVideo();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ファイルの読み込みに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                Console.WriteLine($"[エラー] ファイル読み込み失敗: {ex}");
            }
            finally
            {
                ShowLoading(false);
            }
        }

        /// <summary>
        /// ファイル選択ダイアログ
        /// </summary>
        private bool TryOpenVideoFile(out string filePath)
        {
            filePath = null;
            using var dialog = new CommonOpenFileDialog
            {
                Title = "動画を選択してください",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                IsFolderPicker = false
            };

            if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
                return false;

            filePath = dialog.FileName;
            return true;
        }

        /// <summary>
        /// ビットマップやスライダーを初期化し再生を開始
        /// </summary>
        private async Task InitializeAndStartVideo()
        {
            var source = PresentationSource.FromVisual(this);
            var matrix = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

            int dpiX = (int)Math.Round(96 / matrix.M11);
            int dpiY = (int)Math.Round(96 / matrix.M22);

            // Bitmap生成（GPU/DX経由も対応）。描画方式はメニューの選択に従う。
            _imageSource = await _videoPlayController.CreateBitmapAsync(dpiX, dpiY, _renderTargetType);
            VideoImage.Source = _imageSource;

            // 再生時間表示更新
            TimeSpan total = _videoPlayController.VideoInfo.Duration.ToTimeSpan();
            TotalDurationDisplay = FormatTime(total);

            // シークバー更新ループ開始
            _ = UpdateSeekSliderLoopAsync();

            // ウィンドウタイトル更新
            this.Title = Path.GetFileName(_videoPlayController.VideoInfo.FilePath);

            // 再生開始
            await _videoPlayController.Play();
            Console.WriteLine($"[再生開始] {_videoPlayController.VideoInfo.FilePath}");
        }

        /// <summary>
        /// Spaceキーで再生/一時停止
        /// </summary>
        private async void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                e.Handled = true;
                await TogglePlayPauseAsync();
                return;
            }

            if (e.Key == Key.Right)
            {
                e.Handled = true;
                await StepForwardOneFrameAsync();
            }
        }

        /// <summary>
        /// コマ送り（メニュー用）
        /// </summary>
        private async void StepForward_Click(object sender, RoutedEventArgs e) =>
            await StepForwardOneFrameAsync();

        /// <summary>
        /// コマ送りを 1 回実行し、シークバーと時間表示を追従させる
        /// </summary>
        private async Task StepForwardOneFrameAsync()
        {
            if (_videoPlayController == null) return;

            try
            {
                if (!await _videoPlayController.StepForwardAsync())
                {
                    Console.WriteLine("[コマ送り] 次のフレームを取得できませんでした。");
                    return;
                }

                long current = _videoPlayController.FrameIndex;
                SeekSlider.Value = current;

                double fps = _videoPlayController.VideoInfo.VideoStreams.FirstOrDefault()?.Fps ?? 0;
                if (fps > 0)
                    CurrentTimeDisplay = FormatTime(TimeSpan.FromSeconds(current / fps));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[エラー] コマ送り: {ex}");
            }
        }

        /// <summary>
        /// 再生・一時停止切り替え（ボタン用）
        /// </summary>
        private async void KeyDown_Space(object sender, RoutedEventArgs e)
        {
            await TogglePlayPauseAsync();
        }
        private async Task TogglePlayPauseAsync()
        {
            try
            {
                if (_videoPlayController.IsPaused || !_videoPlayController.IsPlaying)
                {
                    Console.WriteLine("[再生制御] 再開");
                    await _videoPlayController.Play();
                }
                else
                {
                    Console.WriteLine("[再生制御] 一時停止");
                    _videoPlayController.Pause();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"再生切替中にエラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                Console.WriteLine($"[エラー] 再生切替: {ex}");
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("[再生制御] 停止");
            _videoPlayController?.Stop();
        }

        private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
            isDraggingSlider = true;

        private async void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            isDraggingSlider = false;
            long targetFrame = (long)SeekSlider.Value;

            ShowLoading(true);
            bool success = await _videoPlayController.SeekToExactFrameAsync(targetFrame);

            if (success)
            {
                Console.WriteLine($"[シーク] フレーム {targetFrame} に移動成功");

                double fps = _videoPlayController.VideoInfo.VideoStreams.FirstOrDefault()?.Fps ?? 0;
                if (fps > 0)
                    CurrentTimeDisplay = FormatTime(TimeSpan.FromSeconds(targetFrame / fps));
            }
            else
            {
                Console.WriteLine($"[シーク] フレーム {targetFrame} に失敗");
            }
            ShowLoading(false);
        }

        /// <summary>
        /// 時間表示フォーマット (hh:mm:ss または mm:ss)
        /// </summary>
        private string FormatTime(TimeSpan ts) =>
            ts.Hours > 0
                ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";

        /// <summary>
        /// シークスライダー更新ループ
        /// </summary>
        private async Task UpdateSeekSliderLoopAsync()
        {
            isUpdatingSlider = true;
            SeekSlider.Minimum = 0;

            while (isUpdatingSlider && _videoPlayController != null)
            {
                try
                {
                    // 再生/停止アイコン更新
                    PlayPauseIcon.Kind = _videoPlayController.IsPlaying
                        ? MaterialDesignThemes.Wpf.PackIconKind.Pause
                        : MaterialDesignThemes.Wpf.PackIconKind.Play;

                    // スライダー最大値 = 総フレーム数
                    SeekSlider.Maximum = _videoPlayController.GetTotalFrameCount();

                    // 再生中かつユーザー操作中でなければ自動更新
                    if (!isDraggingSlider && _videoPlayController.IsPlaying)
                    {
                        long currentFrame = _videoPlayController.FrameIndex;
                        await Dispatcher.InvokeAsync(() => SeekSlider.Value = currentFrame);
                    }

                    // 現在フレームに基づき時間表示更新
                    double fps = _videoPlayController.VideoInfo.VideoStreams.FirstOrDefault()?.Fps ?? 0;
                    long displayFrame = (long)SeekSlider.Value;

                    CurrentTimeDisplay = fps > 0
                        ? FormatTime(TimeSpan.FromSeconds(displayFrame / fps))
                        : "00:00";
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[エラー] スライダー更新ループ: {ex}");
                }

                await Task.Delay(100); // 更新間隔100ms
            }
        }

        private void Exit(object sender, RoutedEventArgs e) =>
            Application.Current.Shutdown();
    }
}
