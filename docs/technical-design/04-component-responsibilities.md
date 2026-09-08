# 4. レイヤ / コンポーネント責務

## 4.1 App / Presentation境界

- Viewはレイアウト、入力、フォーカス、ポップアップ配置、テーマResourceDictionaryだけを担当する。

- ViewModelはコマンド、画面状態、表示用コレクションを担当し、Provider SDK型、File、ProtectedData、HttpClientを参照しない。

- CalendarPresentationServiceは純粋な表示規則を担当し、WPF BrushやDispatcherを返さない。色はARGB値、進捗は0～1、文字列キーはリソースキーとして返す。

## 4.2 Application service境界

- CalendarSyncServiceは取得、通常リトライ、レート制限、部分成功マージ、キャッシュ更新、同期状態を担当する。

- SyncSchedulerHostedServiceは起動、定期、スリープ復帰、起動時オフライン再試行のトリガーだけを担当する。

- MinuteRefreshHostedServiceは分境界に内部更新要求を発行する。外部同期は呼ばない。

- PresentationRefreshCoordinatorは最新スナップショットを変換し、UIスレッドで差分適用する。

## 4.3 Infrastructure / Provider境界

- SettingsJsonStore / AccountCacheJsonStoreはJSON I/O、原子的置換、破損・未来版退避、同一パス書込み直列化を担当する。

- DpapiTokenStoreは認証ペイロードの暗号化・復号・方式識別・退避を担当する。設定JSONにはtokenRefだけを置く。

- Providerはサービス固有の認証・ページング・日付・色・参加状態・URLを共通モデルへ変換する。

- OSサービスは単一インスタンス、URI起動、自動起動、モニタ/DPI、テーマ・時刻変更通知を薄くラップする。

> **禁止依存**  Core→App、Core→Infrastructure、Core→Providers、Google Provider↔Microsoft Providerの参照は禁止する。AppがComposition Rootとして具体実装を結線する。
