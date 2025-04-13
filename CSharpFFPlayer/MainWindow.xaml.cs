using CSharpFFPlayer;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CSharpFFPlayer
{
    public partial class MainWindow : Window
    {
        private VideoPlayController _videoPlayController;
        private WriteableBitmap _writeableBitmap;

        public MainWindow()
        {
            InitializeComponent();
            _videoPlayController = new VideoPlayController();

            // ファイル選択ダイアログを表示
            if (!TryOpenVideoFile(out string filePath))
            {
                Close(); // ファイル選択がキャンセルされたら終了
                return;
            }

            // 動画ファイルを開く
            try
            {
                _videoPlayController.OpenFile(filePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ファイルのオープンに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            // ウィンドウ表示後の初期化を登録
            Loaded += LoadedProc;
        }

        /// <summary>
        /// ファイル選択ダイアログを表示して、選択されたファイルのパスを取得する。
        /// </summary>
        private bool TryOpenVideoFile(out string filePath)
        {
            filePath = null;
            using (var dialog = new CommonOpenFileDialog
            {
                Title = "動画を選択してください",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                IsFolderPicker = false,
            })
            {
                if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
                    return false;

                filePath = dialog.FileName;
                return true;
            }
        }

        /// <summary>
        /// ウィンドウがアクティブになったタイミングで再生開始処理を実行する。
        /// </summary>
        private async void LoadedProc(object sender, RoutedEventArgs e)
        {
            // ウィンドウがアクティブになるまで待機（最大5秒）
            await Task.Run(() =>
            {
                int waitTimeMs = 0;
                while (!Application.Current.Dispatcher.Invoke(() => IsActive))
                {
                    Thread.Sleep(100);
                    waitTimeMs += 100;
                    if (waitTimeMs > 5000)
                        break;
                }
            });

            try
            {
                InitializeAndStartVideo();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"動画の初期化中にエラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        /// <summary>
        /// 動画描画に必要なリソースを初期化し、再生を開始する。
        /// </summary>
        private async void InitializeAndStartVideo()
        {
            // 現在のDPIを取得し、WriteableBitmapの作成に使用
            var source = PresentationSource.FromVisual(this);
            var matrix = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            int dpiX = (int)Math.Round(96 / matrix.M11);
            int dpiY = (int)Math.Round(96 / matrix.M22);

            // WriteableBitmapを生成し、描画対象に設定
            _writeableBitmap = _videoPlayController.CreateBitmap(dpiX, dpiY);
            VideoImage.Source = _writeableBitmap;

            // 動画再生を開始
            await _videoPlayController.Play();
        }

        /// <summary>
        /// Spaceキーで再生/一時停止を切り替える処理。
        /// </summary>
        private async void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                try
                {
                    if (_videoPlayController.IsPaused || !_videoPlayController.IsPlaying)
                    {
                        await _videoPlayController.Play(); // 再生または再開
                    }
                    else
                    {
                        _videoPlayController.Pause(); // 一時停止
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"再生切替中にエラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        /// <summary>
        /// 停止ボタンのクリックイベントで動画を停止。
        /// </summary>
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _videoPlayController.Stop();
        }

        /// <summary>
        /// シークバーのドラッグ開始時に seeking フラグを立てる。
        /// </summary>
        private void SeekBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _videoPlayController.isSeeking = true;
        }

        /// <summary>
        /// シークバーのドラッグ終了時に seeking フラグを下ろす。
        /// </summary>
        private void SeekBar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _videoPlayController.isSeeking = false;
        }
    }
}
