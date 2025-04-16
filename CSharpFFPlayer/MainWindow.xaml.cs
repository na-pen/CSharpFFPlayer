﻿using CSharpFFPlayer;
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
    public partial class MainWindow : MahApps.Metro.Controls.MetroWindow
    {
        private VideoPlayController? _videoPlayController = null;
        private WriteableBitmap _writeableBitmap;

        public MainWindow()
        {
            InitializeComponent();

        }

        private void Open(object sender, RoutedEventArgs e)
        {

            // ファイル選択ダイアログを表示
            if (!TryOpenVideoFile(out string filePath))
            {
                return;
            }

            // 動画ファイルを開く
            try
            {
                _videoPlayController?.Stop();
                _videoPlayController = new VideoPlayController();
                _videoPlayController.OpenFile(filePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ファイルのオープンに失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            try
            {
                InitializeAndStartVideo();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"動画の初期化中にエラーが発生しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
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

            _ = UpdateSeekSliderLoopAsync();

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

        private async void KeyDown_Space(object sender, RoutedEventArgs e)
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

        /// <summary>
        /// 停止ボタンのクリックイベントで動画を停止。
        /// </summary>
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _videoPlayController.Stop();
        }

        private bool isDraggingSlider = false;
        private bool isUpdatingSlider = false;

        private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            isDraggingSlider = true;
            //_videoPlayController.Pause(); // 一時停止
        }

        private async void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            isDraggingSlider = false;

            long targetFrame = (long)SeekSlider.Value;
            Console.WriteLine($"{targetFrame}");
            bool success = await _videoPlayController.SeekToExactFrameAsync(targetFrame);

            if (success)
            {
                Console.WriteLine($"[シークバー] フレーム {targetFrame} にシーク成功");
            }
            else
            {
                Console.WriteLine($"[シークバー] シーク失敗");
            }
        }

        private async Task UpdateSeekSliderLoopAsync()
        {
            isUpdatingSlider = true;
            SeekSlider.Minimum = 0;

            while (isUpdatingSlider)
            {
                long totalFrames = _videoPlayController.GetTotalFrameCount();
                SeekSlider.Maximum = totalFrames;

                if (!isDraggingSlider && _videoPlayController.IsPlaying)
                {
                    // 現在のフレーム位置を取得してスライダーに反映
                    long currentFrame = _videoPlayController.FrameIndex;

                    // UIスレッドで操作（念のため Dispatcher 使用）
                    await Dispatcher.InvokeAsync(() =>
                    {
                        SeekSlider.Value = currentFrame;
                    });
                }

                await Task.Delay(100); // 100msごとに更新
            }
        }

        private void Exit(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }
    }
}
