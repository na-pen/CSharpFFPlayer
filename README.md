# CSharpFFPlayer
 FFmpegを用いた動画プレイヤーです。ふと欲しくなったので作りました。    
 動画ファイルからFFmpegを利用して音声と映像のフレームを取得し、それを描画します。

 ## 機能
  - FFmpegが対応している映像/音声形式の動画の再生/一時停止/再開/シーク
  - GPU使用可能時、GPUで映像をデコードして再生する
  - 音声と映像の誤差2フレーム以内での同期再生

 ### GPU使用について
 再生端末に、NVIDIA / Intel / AMD のいずれかのGPUが搭載してあり、次のデコーダが使用可能な動画の場合は、GPUで映像をデコードし、再生10フレーム前まではGPUメモリでフレームを保持します。    
  - h264_cuvid
  - hevc_cuvid
  - h264_qsv
  - hevc_qsv
  - h264_amf
  - hevc_amf

 ## 今後追加予定の機能
  - コマ送り/コマ戻し
  - 音量調整
  - 音声再生デバイス変更
  - 簡易色調補正
  - わかりやすいドキュメント

 ## 参考にした資料
  - https://qiita.com/Kujiro/items/78956f5906538b718ffb
