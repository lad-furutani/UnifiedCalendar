# 5. 主要クラス・インターフェースと依存関係

| 型 | 責務 / 主要メソッド |
| --- | --- |
| ICalendarProvider | ProviderKind、AuthenticateAsync、ReauthenticateAsync、ListCalendarsAsync、GetEventsAsync。SDK型を返さない。 |
| CalendarSyncService | RequestSyncAsync(reason)、アカウント4並列、結果のステージング、部分成功マージ、Cache保存、SyncState更新。 |
| CalendarPresentationService | BuildSnapshot(source, settings, now, localZone)。フィルタ、所属日、ソート、色、進捗、表示派生値を生成。 |
| ISettingsStore | LoadAsync / SaveAsync。スキーマ判定・移行・原子的保存・退避をStore内部で完結。 |
| ICacheStore | LoadAccountAsync / SaveAccountAsync / RemoveAccountAsync。アカウント別スナップショット。 |
| ITokenStore | ReadAsync / WriteAsync / RemoveAsync。Providerごとの不透明ペイロードをDPAPI CurrentUserで保護。 |
| SnapshotDiffer | 前回/今回の安定キー列を比較し、Add/Update/Move/Remove計画を返す純粋ロジック。 |
| IUiDispatcher | InvokeAsync(Func<Task>)。WPF DispatcherをCoreへ露出させない。 |
| IWindowPlacementService | DIP位置保存、モニタ選択、DPI変換、作業領域クランプ。 |
| ISingleInstanceService | CurrentUser単位Mutex取得、名前付きPipeによるActivate要求。 |
| IExternalUriLauncher | 絶対http/https URIだけを既定ブラウザへ渡す。 |
| MainWindowViewModel | ステータス、TimelineItems、手動更新、詳細選択、空/初回取得状態。 |
| SettingsWindowViewModel | 編集用コピー、即時反映項目、OK/Apply/Cancel、6カテゴリ、未適用変更追跡。 |

## 5.1 DIライフタイム

- Singleton: TimeProvider.System、Store、TokenStore、各Provider、CalendarSyncService、CalendarPresentationService、Scheduler、OSサービス、MainWindowViewModel。

- SettingsWindowViewModelは同時1画面要件に合わせSingleton。開くたびに編集セッションを開始し、既存画面があれば前面化する。

- Transient: ルール編集ViewModel、確認ダイアログViewModel、短命なProvider request context。

- HttpClientはIHttpClientFactoryの名前付きクライアントをProvider別に登録し、Timeout=30秒を固定する。SDKが独自クライアントを持つ場合も同一設定を注入する。

## 5.2 Generic Hostの起動・終了

1.  App.OnStartupで単一インスタンスを先に判定する。第二プロセスはActivateを送ってHostを構築せず終了する。

2.  第一プロセスだけがHostをBuild/Startし、設定・キャッシュを読み込んで最初の表示スナップショットを作る。

3.  MainWindowを生成して表示し、起動同期をバックグラウンド要求する。キャッシュなしなら取得中表示。

4.  トレイの終了でルートCancellationTokenをCancelし、同期を停止。進行中アカウントのステージング結果はコミットしない。

5.  ウィンドウ状態と確定済み設定を保存し、Host.StopAsync、Dispose、Log.CloseAndFlushを順に実行する。
