using CSharpFFPlayer;
using Microsoft.WindowsAPICodePack.Dialogs;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

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

            using (var cofd = new CommonOpenFileDialog()
            {
                Title = "動画を選択してください",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                // フォルダ選択モードにする
                IsFolderPicker = false,
            })
            {
                if (cofd.ShowDialog() != CommonFileDialogResult.Ok)
                {
                    return;
                }

                // 動画ファイルを開く
                _videoPlayController.OpenFile(cofd.FileName);
            }
            
            Loaded += LoadedProc;

        }

        private async void LoadedProc(object sender, RoutedEventArgs e)
        {
            // WindowがActiveになるまで待つ
            await Task.Run(() =>
            {
                do
                {
                    Thread.Sleep(100);
                } while (!Application.Current.Dispatcher.Invoke(() => { return IsActive; }));
            });
            // DPI取得
            var presentationSource = PresentationSource.FromVisual(this);
            Matrix matrix = presentationSource.CompositionTarget.TransformFromDevice;
            var dpiX = (int)Math.Round(96 * (1 / matrix.M11));
            var dpiY = (int)Math.Round(96 * (1 / matrix.M22));

            // WriteableBitmap作成
            _writeableBitmap = _videoPlayController.CreateBitmap(dpiX, dpiY);

            // ImageコントロールのSourceにWriteableBitmapを設定
            VideoImage.Source = _writeableBitmap;
            // 動画再生を開始
            await _videoPlayController.Play();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // DPI取得
            var presentationSource = PresentationSource.FromVisual(this);
            Matrix matrix = presentationSource.CompositionTarget.TransformFromDevice;
            var dpiX = (int)Math.Round(96 * (1 / matrix.M11));
            var dpiY = (int)Math.Round(96 * (1 / matrix.M22));

            // WriteableBitmap作成
            _writeableBitmap = _videoPlayController.CreateBitmap(dpiX, dpiY);

            // ImageコントロールのSourceにWriteableBitmapを設定
            VideoImage.Source = _writeableBitmap;
            // 動画再生を開始
            await _videoPlayController.Play();
        }
        // Spaceキーで再生と一時停止を切り替える
        private async void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Space)
            {
                if (_videoPlayController.IsPaused || !_videoPlayController.IsPlaying)
                {
                    // 再生または再開
                    await _videoPlayController.Play();
                }
                else
                {
                    // 一時停止
                    _videoPlayController.Pause();
                }
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _videoPlayController.Stop();
        }

        private void SeekBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _videoPlayController.isSeeking = true;
        }

        private void SeekBar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _videoPlayController.isSeeking = false;
            //_videoPlayController.SeekToSeconds(targetSeconds);
        }
    }
}

