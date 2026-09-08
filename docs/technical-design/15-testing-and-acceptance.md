# 15. テスト戦略とACC-001～ACC-013マトリクス

## 15.1 テスト層

- Core unit: Frozen/Fake TimeProviderで日付、終了、進捗、ゼロ時間、複数日、色、ソート、安定ID、差分を決定的に検証。

- Service unit: NSubstituteのProvider/Storeで成功、部分成功、タイムアウト、429、キャンセル、重複要求集約を検証。実時間sleepは禁止。

- Storage integration: テスト専用一時ディレクトリでatomic replace、途中失敗、破損、future schema、settings migration、DPAPI round-tripを検証。

- Provider contract: 保存済みの匿名化fixtureとfake HTTP handlerでページング、繰り返し、cancelled、色、参加状態、URL変換を検証。SDK型はテストプロジェクト内に留める。

- WPF smoke/manual: Windows 11実機でテーマ、DPI、1000件、focus、tray、browser、OAuth実アカウントを確認。画面自動化ライブラリは初期版へ追加しない。

## 15.2 受入基準マトリクス

| ID | 観点 | 検証 |
| --- | --- | --- |
| ACC-001 | 複数アカウント | Fake 4アカウント統合unit + Google 2/Microsoft 2の手動integration。G/M識別と混在表示。 |
| ACC-002 | 並び順 | PresentationTheory: 日付、終日、開始、件名、進行中非繰上げ、安定tie-break。 |
| ACC-003 | 経時表示 | FakeTimeProviderを分進行。進捗増加、endで削除、終日progress null。 |
| ACC-004 | 複数日 | 終日1件、時刻指定開始日1件、過去開始継続中→今日、終了後削除。 |
| ACC-005 | 部分成功 | 2 calendar中1成功1失敗。成功更新、失敗cache保持、該当stateのみwarning。 |
| ACC-006 | オフライン起動 | cache有/無のStartupOrchestrator + UI state。API未完了でも起動完了。 |
| ACC-007 | 原子的保存 | Flush後/Replace前のfault injection、旧本体可読、corrupt/future quarantineで継続。 |
| ACC-008 | 更新安定性 | SnapshotDifferでResetなし、同一VM参照維持。実機でscroll anchor/focus確認。 |
| ACC-009 | テーマ/DPI | 実機でlight/dark切替、100/150/200%モニタ移動、24 DIPでも欠けなし。 |
| ACC-010 | 単一instance | 2 process integration。pipe activate、第二process終了、Store同時writeなし。 |
| ACC-011 | 参照専用 | UI/command/API scope/installerのreview。write scopeと編集/export/pushがない。**通知は開始前の表示のみで、通知からの操作導線がない。** |
| ACC-012 | keyboard | 実機keyboard checklist: Tab/矢印/Enter/Esc/Ctrl+C、focus可視、Space無動作。 |
| ACC-013 | 開始前通知 | Core unitで境界を検証: 通知時刻ちょうど、開始ちょうど、終日除外、重複、集約、抑制の初期化。実機でバルーン表示と集中モード時の挙動。 |

## 15.3 品質ゲート

- dotnet build -c Release -f net8.0-windows -r win-x64がwarning 0（既知SDK warningは理由と抑制範囲を文書化）。

- dotnet test -c Release -f net8.0-windowsが全成功。外部APIへ接続するテストは既定test suiteから分離する。

- 1,000件性能目標、30秒timeout、3 retry delay、4並列を計測テストで確認する。

- Release候補でACC-001～013をマトリクスどおり実施し、証跡をdocs/acceptance/へ残す。
