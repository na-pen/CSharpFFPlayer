using CSharpFFPlayer;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        private WriteableBitmap _writeableBitmap;

        private bool isDraggingSlider = false;
        private bool isUpdatingSlider = false;

        public event PropertyChangedEventHandler? PropertyChanged;

        private string _currentTimeDisplay;
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

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public MainWindow()
        {
            InitializeComponent();
        }

        private void ShowLoading(bool isVisible)
        {
            LoadingOverlay.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// メニューバー「開く」やボタンで呼ばれる動画ファイル選択処理
        /// </summary>
        private async void OpenVideo(object sender, RoutedEventArgs e)
        {
            if (!TryOpenVideoFile(out string filePath))
                return;


            ShowLoading(true); // ← 開始時に表示
            try
            {
                _videoPlayController?.Stop();

                _videoPlayController = await Task.Run(() =>
                {
                    var controller = new VideoPlayController();
                    controller.OpenFile(filePath);
                    return controller;
                });

                await InitializeAndStartVideo();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ファイルの読み込みに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ShowLoading(false); // ← 終了時に非表示
            }
        }


        /// <summary>
        /// ファイル選択ダイアログを表示し、選択されたファイルパスを返す
        /// </summary>
        private bool TryOpenVideoFile(out string filePath)
        {
            filePath = null;
            using (var dialog = new CommonOpenFileDialog
            {
                Title = "動画を選択してください",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                IsFolderPicker = false
            })
            {
                if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
                    return false;

                filePath = dialog.FileName;
                return true;
            }
        }

        /// <summary>
        /// 動画再生のためのビットマップやスライダーなどを初期化し、再生を開始
        /// </summary>
        private async Task InitializeAndStartVideo()
        {
            var source = PresentationSource.FromVisual(this);
            var matrix = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

            int dpiX = (int)Math.Round(96 / matrix.M11);
            int dpiY = (int)Math.Round(96 / matrix.M22);

            _writeableBitmap = _videoPlayController.CreateBitmap(dpiX, dpiY);
            VideoImage.Source = _writeableBitmap;

            TimeSpan total = _videoPlayController.VideoInfo.Duration.ToTimeSpan();
            TotalDurationDisplay = FormatTime(total);
            ShowLoading(false);
            _ = UpdateSeekSliderLoopAsync();
            this.Title = Path.GetFileName(_videoPlayController.VideoInfo.FilePath);

            await _videoPlayController.Play();
        }


        /// <summary>
        /// Space キーで再生・一時停止を切り替える
        /// </summary>
        private async void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
                await TogglePlayPauseAsync();
        }

        /// <summary>
        /// 再生・一時停止切り替え（ボタン用）
        /// </summary>
        private async void KeyDown_Space(object sender, RoutedEventArgs e)
        {
            await TogglePlayPauseAsync();
        }

        /// <summary>
        /// 再生・一時停止をトグル
        /// </summary>
        private async Task TogglePlayPauseAsync()
        {
            try
            {
                if (_videoPlayController.IsPaused || !_videoPlayController.IsPlaying)
                    await _videoPlayController.Play(); // 再開
                else
                    _videoPlayController.Pause();      // 一時停止
            }
            catch (Exception ex)
            {
                MessageBox.Show($"再生切替中にエラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 停止ボタンで動画を停止
        /// </summary>
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _videoPlayController?.Stop();
        }

        /// <summary>
        /// シークスライダー操作開始時にドラッグ中フラグをON
        /// </summary>
        private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            isDraggingSlider = true;
        }

        /// <summary>
        /// シークスライダー操作終了時、指定フレームへシーク
        /// </summary>
        private async void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            isDraggingSlider = false;

            long targetFrame = (long)SeekSlider.Value;

            ShowLoading(true); // ← 開始時に表示
            bool success = await _videoPlayController.SeekToExactFrameAsync(targetFrame);

            if (success)
            {
                Console.WriteLine($"[シークバー] フレーム {targetFrame} にシーク成功");

                double fps = _videoPlayController.VideoInfo.VideoStreams.FirstOrDefault()?.Fps ?? 0;
                if (fps > 0)
                {
                    CurrentTimeDisplay = FormatTime((long)(targetFrame / fps));
                }
            }
            else
            {
                Console.WriteLine($"[シークバー] シーク失敗");
            }
            ShowLoading(false);
        }

        /// <summary>
        /// フレーム数と TimeBase から再生時間を計算し、hh:mm:ss または mm:ss 形式で返す
        /// </summary>
        private string FormatTime(long seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);

            return FormatTime(ts);
        }

        /// <summary>
        /// フレーム数と TimeBase から再生時間を計算し、hh:mm:ss または mm:ss 形式で返す
        /// </summary>
        private string FormatTime(TimeSpan ts)
        {
            return ts.Hours > 0
                ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

        /// <summary>
        /// 再生状態やフレーム数に基づいてスライダーとアイコンを定期更新するループ
        /// </summary>
        private async Task UpdateSeekSliderLoopAsync()
        {
            isUpdatingSlider = true;
            SeekSlider.Minimum = 0;

            while (isUpdatingSlider)
            {
                if (_videoPlayController != null)
                {
                    PlayPauseIcon.Kind = _videoPlayController.IsPlaying
                        ? MaterialDesignThemes.Wpf.PackIconKind.Pause
                        : MaterialDesignThemes.Wpf.PackIconKind.Play;

                    long totalFrames = _videoPlayController.GetTotalFrameCount();
                    SeekSlider.Maximum = totalFrames;

                    long displayFrame = (long)SeekSlider.Value;

                    // 再生中でスライダー操作していないときだけ自動で更新
                    if (!isDraggingSlider && _videoPlayController.IsPlaying)
                    {
                        long currentFrame = _videoPlayController.FrameIndex;

                        await Dispatcher.InvokeAsync(() =>
                        {
                            SeekSlider.Value = currentFrame;
                        });

                        displayFrame = currentFrame; // 表示も現在位置に合わせる
                    }

                    // SeekSlider.Value をもとに現在時刻を表示
                    await Dispatcher.InvokeAsync(() =>
                    {
                        double fps = _videoPlayController.VideoInfo.VideoStreams.FirstOrDefault()?.Fps ?? 0;
                        if (fps > 0)
                        {
                            var seconds = displayFrame / fps;
                            CurrentTimeDisplay = FormatTime((long)seconds);
                        }
                        else
                        {
                            CurrentTimeDisplay = "00:00";
                        }
                    });
                }

                await Task.Delay(100);
            }
        }


        /// <summary>
        /// メニュー「終了」でアプリを終了
        /// </summary>
        private void Exit(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}
