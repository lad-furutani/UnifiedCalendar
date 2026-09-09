# 13. ウィンドウ・トレイ・単一インスタンス・DPI

## 13.1 メイン / 設定 / ポップアップ

- メインは通常WPF Window。**最大化可（1.2.0で変更。1.1.0までは最大化不可だった。下記と WIN-001 / WIN-001a を正とする）**、最前面なし、右端強制/AppBarなし。×はHide、最小化はタスクバー、完全終了はトレイの終了だけ。

- **メインの寸法（DIP）。** 初期820×640、最小**370**×400（**1.1.0で最小幅を560から370へ変更**）。設定画面は初期760×560、最小640×440。値は`LayoutMetrics`を正とする。**最小幅を成立させるため、ステータス領域の最終正常更新時刻だけを可変幅にし、幅不足時は省略記号で切る。現在時刻・同期状態・手動更新ボタン・設定ボタンは切らない。** 詳細ポップアップの最大幅520は変更せず、メインウィンドウより広くなることを許容する。

- **既知の制限（2026-09-09 実測）。** **フォントサイズを最大の24 DIPにしたうえで最小幅まで縮めると、時刻列が202 DIPを占め、件名に残る幅が約47 DIPになる。** 行の時刻・注意アイコン・会議ボタン・取得元記号は表示されたままで、件名だけが省略記号で切れる。最大フォントと最小幅を同時に選んだ場合だけ起き、ウィンドウを広げれば戻るため、**最小幅をフォントサイズ依存にする案（`Window.MinWidth`の動的束縛）は採らず、この制限を受け入れる。** 14 DIPでは件名に約130 DIPが残る。 **2026-09-09の実機受入で「実用上困らない」と評価され、据え置きを確定した**（`acceptance-1.1.0.md` PP-71）。

- **ステータス領域の3つのボタンは同じ高さで揃える（1.2.0で変更）。** 同期状態・設定は`LayoutMetrics.CalculateWarningButtonHeight`（`30 + (fontSize - 14) * 1.5` DIP）を束縛しており、**手動更新も同じ値を束縛する。** 1.1.0までは手動更新だけが`Padding="10,5"`による自動サイズで、既定フォントで約1〜2 DIP高かった。**横のPaddingは10のまま変えない**（`StatusMinimumWidthCalculator`が左右合計20 DIPを定数で持ち、最小幅370の成立に使っている）。**縦のPaddingは5から4へ下げた（2026-09-09 実測）。** 縦5のままでは希望高が10 / 14 / 24 DIPで25.30 / 30.62 / 43.92 DIPとなり、**10 DIPで固定高24 DIPを1.30 DIP超えて文字が切れる。** 縦4では23.30 / 28.62 / 41.92 DIPとなり全水準で収まる。**余裕は0.70 / 1.38 / 3.08 DIPで、最小フォントがいちばん厳しい。`FontFamily`、`CalculateWarningButtonHeight`の式、この縦Paddingのいずれかを変えるときは、10 DIPの余裕が正であることを測り直すこと**（`Phase7bRemediationTests`の`StatusButtonsShareHeightAndRefreshTextFitsAtSupportedFontSizes`が守っている）。

- **最大化を許可する（1.2.0で変更。WIN-001 / WIN-001a）。** 1.1.0までは`DisableMaximizeButton`が`WS_MAXIMIZEBOX`を外していた。**このビットが無いとWindowsのスナップ（上端を画面上端までドラッグして高さを画面いっぱいにする操作）が働かない。** スナップだけを許可する方法はないため、最大化ボタン・タイトルバーのダブルクリック・Win+↑・左右半分へのスナップもあわせて有効になる。
  - **最大化状態は保存しない。** `SchedulePlacementSave`は`WindowState != Normal`のとき保存せず、`ApplyRestoreBounds`は復元時に`WindowState.Normal`へ戻す。**この2つの挙動は変えない。** 結果として、最大化して終了しても次回は最大化前の通常サイズで起動する。
  - **スナップした大きさは保存し、真の最大化は保存しない（1.2.0で決定。TBD-021、2026-09-09 実測）。**
    - **実測結果。** 上下方向最大化と左右半分へのスナップはいずれも`showCmd = SW_SHOWNORMAL`（`WindowState.Normal`、`IsZoomed=False`）で、**真の最大化だけが`SW_SHOWMAXIMIZED`（`Maximized`、`IsZoomed=True`）である。** 判別できる（`work/docs/investigation-snap-placement.md` TT-1〜TT-6）。
    - **`Window.RestoreBounds`はWin32の`WINDOWPLACEMENT.rcNormalPosition`（最大化・最小化する前の矩形）であり、`WindowState`が`Normal`でもスナップ前の矩形を返す。** Windowsはスナップ時に実際のウィンドウ矩形だけを変え、この矩形を保つ（そうでないとスナップ解除で元へ戻せない）。**「Normalなら現在の矩形が返る」というのは誤りである。**
    - **したがって`Capture`は、`WindowState`が`Normal`のときは実際のウィンドウ矩形を、それ以外のときは`RestoreBounds`を取る。** 非スナップの`Normal`では両者が一致するので挙動は変わらない。最大化・最小化では従来どおり直前の通常矩形を保存し、**WIN-001aの「最大化状態は保存しない」を維持する。**
    - **左右半分へのスナップは幅と位置も変える**（実測で 781×1686、L=-6）。高さだけの話ではない。**復元されるのは大きさと位置で、スナップ状態そのものではない。**
    - **メインウィンドウと設定画面の両方が対象である。** `MainWindowPlacementService.Capture`と`SettingsWindowPlacementService.Capture`が同じ書き方をしている。**設定画面は`ResizeMode="CanResize"`のままで最大化ボックスを外しておらず（1.0.0から）、当初からスナップできて大きさが保存されない状態だった。** 設定画面の位置・サイズ保存はUI-015で、メインと同じ意図である。
    - **`Capture`が`RestoreBounds`を使っていたのは1.0.0（`28710b8`）からである。** 1.2.0が持ち込んだ退行ではなく、最大化を許可したことで初めて到達できる経路になった。
  - **終了時の保存（`PrepareForApplicationExitAsync`）は`WindowState`を見ずに必ず走る。** ×でウィンドウを隠してからトレイで終了する経路が通常であり、**隠れている状態でも正しい矩形が取れることを確かめる必要がある。**
  - **最小高さ400 DIPの下限は効いたままである。**

- 設定はOwnerをMainWindowにしたモードレスWindowを1つだけ保持する。Owner非表示中も操作可能状態を維持し、再度開くとActivateする。

- 詳細はPopupまたは透過子Windowのどちらかに混在させず、初期実装はPlacementTarget付きWPF Popupを採用。StaysOpen=false相当の外クリックに加え、Esc/scroll/window変化で明示Closeする。

## 13.2 位置・サイズのDIP保存

1.  **保存する矩形は`WindowState`で決める（1.2.0で変更。TBD-021）。`Normal`のときは実際のウィンドウ矩形、それ以外（`Maximized` / `Minimized`）のときは`RestoreBounds`をWPF DIPで保存する。** monitorDeviceNameと保存時DPIを併記する。最小化状態そのものは復元しない。**1.1.0までは常に`RestoreBounds`を保存しており、スナップした大きさが失われていた**（詳細は13.1と WIN-001a）。

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

- System.Windows.Forms.NotifyIconをWPFから使用し、表示/非表示、今すぐ更新、設定、終了を提供。ダブルクリックは常に表示・前面化。**1.1.0でメイン画面のステータス領域にも設定ボタン（歯車）を置く。トレイメニューの『設定』と同じ`ISettingsWindowLauncher`を通し、導線を二重に実装しない。** 通知は同じ`NotifyIcon`の`ShowBalloonTip`を用いる（19章）。

- トレイ非表示中も外部同期、内部更新、終了除外、キャッシュ更新を継続する。**1.1.0で予定開始前のバルーン通知を追加した（19章）。非表示中も出す。それ以外の通知、サウンドの制御、エラー用のアイコン変更は引き続き行わない。**

- 自動起動はHKCU\Software\Microsoft\Windows\CurrentVersion\Runへ、引用した実行ファイルパスを登録。初期ON。設定変更はIStartupRegistrationServiceで即時反映。

- **スタートアップフォルダへのショートカット方式を2026-09-07に再検討し、レジストリのまま据え置くと決定した。** 当初の懸念（アンインストーラがHKCUの値を消せない）はper-userインストールの決定で解消済みであり、切り替えても解決される問題が無い。一方コストは、.NETにショートカット生成の標準APIが無いため`IShellLink`/`IPersistFile`のCOM相互運用を自前で宣言するか、**ポリシーで無効化されうるWScript.Shell**へ依存することになる。状態判定も、現在の「登録済みコマンドラインと実行中exeパスの完全一致」から、ファイルの有無（古い／別の場所を指すショートカットもONと読める）または再度のCOM解決へ弱まる。加えてH-5の実機確認が未実施のまま残っており、方式を変えると初回の実機確認対象が書き下ろしたばかりのコードになる。**配布先でグループポリシーがRunキーへの書き込みを禁じている場合は再検討する。**

- Inno Setupは{localappdata}\Programs\UnifiedCalendarへper-userインストールし、アンインストール時にユーザーデータを自動削除しない。
