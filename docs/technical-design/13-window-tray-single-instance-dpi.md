# 13. ウィンドウ・トレイ・単一インスタンス・DPI

## 13.1 メイン / 設定 / ポップアップ

- メインは通常WPF Window。最大化不可、最前面なし、右端強制/AppBarなし。×はHide、最小化はタスクバー、完全終了はトレイの終了だけ。

- 設定はOwnerをMainWindowにしたモードレスWindowを1つだけ保持する。Owner非表示中も操作可能状態を維持し、再度開くとActivateする。

- 詳細はPopupまたは透過子Windowのどちらかに混在させず、初期実装はPlacementTarget付きWPF Popupを採用。StaysOpen=false相当の外クリックに加え、Esc/scroll/window変化で明示Closeする。

## 13.2 位置・サイズのDIP保存

1.  WindowState=Normal時のRestoreBoundsをWPF DIPで保存し、monitorDeviceNameと保存時DPIを併記する。最小化状態そのものは復元しない。

2.  復元対象monitorをdevice nameで選ぶ。見つからなければprimary work areaの右端へ初期配置する。

3.  Win32のwork area pxを対象monitor DPIでDIPへ変換し、最小サイズを満たしたboundsを作業領域へclampする。タイトルバーの操作可能部分を必ず残す。

4.  復元時、サイズ（Width/Height）の保存DIP値は換算せずそのまま用いる。拡大率が変わってもDIPサイズを保持し、WM_DPICHANGEDの推奨rect（DIPサイズ保持）と同じ意味に揃える。物理ピクセルサイズを保持する換算は行わない。表示日数・フォントサイズ・密度で決まる情報量が、拡大率設定の変更だけで変わらないことを優先する。

5.  位置（Left/Top）は保存時DPIと復元先monitor DPIの比で換算する。monitor DIP原点自体がそのmonitorのDPIで動くため、この換算で原点移動に追従する。換算後の値は必ず3のclampを通す。

6.  SourceInitialized後にPerMonitorV2の実DPIで最終補正し、WM_DPICHANGEDでは推奨rectを採用して列幅・行MinHeight・Popupを再計算する。推奨rectの適用はPerMonitorV2宣言下のWPFが自身で行うため、アプリ側で重ねてSetWindowPosしない。

app.manifestでPerMonitorV2を宣言する。SystemEvents/Window messageでテーマ、時刻、タイムゾーン変更を検知し、テーマResourceDictionaryまたはPresentationを即時再評価する。

## 13.3 単一インスタンス

- Mutex名: Local\UnifiedCalendar.{CurrentUserSid}。CurrentUser SIDを含め、同一ユーザーだけ1つにする。

- Named Pipe: UnifiedCalendar.Activation.{CurrentUserSid}。第二プロセスはactivateを送信して終了。第一プロセスはDispatcherでShow、WindowState=Normal、Activate、SetForegroundWindowを実行する。

- Pipe ACLはCurrentUserだけ。第一プロセス起動直後のraceは短い再接続を行い、第二プロセスを常駐させない。

## 13.4 タスクトレイと自動起動

- System.Windows.Forms.NotifyIconをWPFから使用し、表示/非表示、今すぐ更新、設定、終了を提供。ダブルクリックは常に表示・前面化。

- トレイ非表示中も外部同期、内部更新、終了除外、キャッシュ更新を継続し、通知・バルーン・音・エラーアイコン変更は行わない。

- 自動起動はHKCU\Software\Microsoft\Windows\CurrentVersion\Runへ、引用した実行ファイルパスを登録。初期ON。設定変更はIStartupRegistrationServiceで即時反映。

- **スタートアップフォルダへのショートカット方式を2026-09-07に再検討し、レジストリのまま据え置くと決定した。** 当初の懸念（アンインストーラがHKCUの値を消せない）はper-userインストールの決定で解消済みであり、切り替えても解決される問題が無い。一方コストは、.NETにショートカット生成の標準APIが無いため`IShellLink`/`IPersistFile`のCOM相互運用を自前で宣言するか、**ポリシーで無効化されうるWScript.Shell**へ依存することになる。状態判定も、現在の「登録済みコマンドラインと実行中exeパスの完全一致」から、ファイルの有無（古い／別の場所を指すショートカットもONと読める）または再度のCOM解決へ弱まる。加えてH-5の実機確認が未実施のまま残っており、方式を変えると初回の実機確認対象が書き下ろしたばかりのコードになる。**配布先でグループポリシーがRunキーへの書き込みを禁じている場合は再検討する。**

- Inno Setupは{localappdata}\Programs\UnifiedCalendarへper-userインストールし、アンインストール時にユーザーデータを自動削除しない。
