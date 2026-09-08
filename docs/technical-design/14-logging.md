# 14. ログ設計

## 14.1 出力

- SerilogのCompactJsonFormatterで1行1イベント。Information以上を既定とし、詳細Debugのユーザー設定は設けない。

- ファイル名unifiedcalendar-.log、RollingInterval.Day、retainedFileCountLimit=30、shared=false。30日を超えるファイルを自動削除する。

- timestampはISO 8601のローカルoffset付き、加えてElapsedMsを数値で記録する。

- **`CompactJsonFormatter` は CLEF 仕様により `@t` を必ずUTCで書くため、そのままでは上の要求を満たさない。** 時刻フィールドだけをローカルoffset付きへ差し替える整形を挟む。値は `LogEvent.Timestamp`（Serilogが記録時に取得したローカルoffset付きの値）をそのまま用い、整形時に現在時刻を取り直さない。`@t` をUTC以外にすると厳密にはCLEFから外れるが、本アプリのログはローカルファイルを人が読む前提であり、Seq等のCLEF対応ツールへ取り込む要件は無い。ファイル名の日付（ローカル日）と中身の日付が食い違わないことを優先する。

## 14.2 必須イベント

| カテゴリ | 主なイベント / property |
| --- | --- |
| Lifecycle | ApplicationStarting/Started/Stopping/Stopped、PreviousRunUnclean。Version、ProcessId。 |
| Sync | AccountSyncStarted/Completed、CalendarFetchCompleted。Provider、InternalAccountId、CalendarKeyHash、Count、ElapsedMs、Result。 |
| Retry | TransientRetryScheduled、RateLimitDeferred。Attempt、DelayMsまたはRetryAtUtc、ErrorCategory。 |
| Auth | AuthenticationStarted/Completed/Required。Provider、InternalAccountId、結果。トークン値なし。 |
| Storage | Settings/Cache/Token Load/Save/Quarantine。schemaVersion、結果、ファイル種別。本文・完全パスなし。 |
| Unexpected | Exception type、sanitized message、stack trace、correlation id。SDK response body/headerは記録しない。 |

## 14.3 プライバシーフィルタ

- メールアドレス、displayName、件名、説明、場所、URL、calendarId、生のeventIdをログへ出さない。

- アカウントはInternalAccountId + Provider、カレンダー/イベントは長さ付き正規化キーのSHA-256先頭12byteをBase64Url化したhashで識別する。

- 例外を{@Exception}で無条件に構造化せず、ExceptionSanitizerが種別・HTTP状態・安全なerror codeへ落とす。認証/HTTP header、query、bodyは破棄する。

- 一般設定の『ログフォルダを開く』『最新ログを開く』はIExternalPathLauncher経由で実装し、ログ編集UIは持たない。
