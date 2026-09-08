# 9. 同期シーケンス、部分成功、リトライ、キャンセル、レート制限

## 9.1 数値と分類

| 項目 | 確定値 / 動作 |
| --- | --- |
| API timeout | 各request 30秒固定。ユーザー設定なし。 |
| 通常リトライ | 初回失敗後に最大3回。待機2秒→5秒→10秒。合計最大4 attempts。 |
| 対象 | ネットワーク一時失敗、HTTP 408、5xx。認証/権限/形式不正は対象外。 |
| レート制限 | 429/Retry-After等は通常カウンタと分離。サービス指定時刻を優先。 |
| 起動時オフライン | 初回同期でNetworkまたはTimeoutになった表示対象カレンダーだけを、30秒間隔で最大3回。その後は通常周期。 |
| アカウント同時上限 | 4。SemaphoreSlimで制限し、待機中はCancellationTokenに応答。 |
| アカウント内 | 初期版はカレンダーを順次取得。レート負荷と部分成功の追跡を単純化。 |
| 内部更新 | ローカル分境界へ揃えて1分ごと。外部APIアクセスなし。 |

## 9.2 起動シーケンス

1.  設定を読み、未知/破損なら退避して既定値を採用する。トークン復号可否をアカウントごとに判定する。

2.  表示対象アカウントのキャッシュを並列読込し、読めた分だけ統合スナップショットを作って即時表示する。

3.  キャッシュが1件もなければ『予定を取得中…』、アカウント0件なら未登録状態を表示する。

4.  Host起動後にStartupトリガーをCalendarSyncServiceへ送り、UIをブロックせず最新同期する。

5.  初回同期でNetworkまたはTimeoutになった表示対象カレンダーだけを、30秒後から最大3回再試行する。

Startup retryの対象単位はカレンダーとする。各回は、その時点でNetworkまたはTimeoutの失敗が残るCalendarId集合だけを処理し、1回の再試行で同一または複数アカウントの複数カレンダーをまとめてよい。同一アカウント内を含め、既に成功したカレンダーおよび再試行対象外のカレンダーは再取得せず、その結果、cache、CalendarSyncStateを維持する。再試行で成功したカレンダーは次回の対象集合から外す。これにより、初回に成功したカレンダーがStartup retryで再取得され、後発の失敗によって失敗状態へ悪化することを禁止する。

公開`ICalendarSyncService` APIがアカウント単位の要求を受ける形でもよいが、Startup retryの内部要求は対象CalendarId集合を保持し、イベント取得をその集合へ絞り込めなければならない。Phase 6向けの公開API追加は要求しない。最大3回、通常リトライ、レート制限、通常周期、Manual更新がRetry-Afterを迂回しない規則は変更しない。

## 9.3 アカウント同期シーケンス

```text
Trigger → account request coalescer → global semaphore(4)
  → token validation
  → selected readable calendars (sequential)
      → request timeout(30s) / retry policy / full paging
      → stage Success(events) or Failure(previous cache retained)
  → if application cancellation: discard whole account stage
  → merge success calendars + previous failed calendars
  → atomic account cache save (only if ≥1 calendar succeeded)
  → publish immutable SyncSnapshot + AccountSyncState
  → presentation refresh → UI diff
```

- 同期中に同一アカウントへの自動周期/復帰要求が来たらpending=trueに集約し、完了後に最大1回だけ再実行する。手動更新ボタンは同期中無効。

- 集約する要求が対象CalendarId集合を持つ場合、pending要求と新規要求の集合を和集合へ統合して1回で処理する。全表示対象カレンダーを対象とする要求と部分集合を対象とする要求が重なった場合は、全表示対象カレンダーを対象とする1回として処理する。部分集合要求どうしが重なった場合も同様に和集合とし、後着要求で先行要求の対象を置き換えて取りこぼしを生じさせてはならない。

- アカウントは完了したものからpublishする。全アカウント完了を待たない。

- 同一アカウント内は全対象カレンダーの試行が終わるまでstageし、アプリ終了キャンセル時の中途結果保存を防ぐ。

- 一部成功では成功カレンダーだけ新値、失敗カレンダーは前回キャッシュを採用。全失敗ならキャッシュを変更しない。

- 並行するアカウント同期のsnapshot publishには単調な順序を与える。新しいsnapshotをpublishした後に、それより古いsnapshotを`SnapshotChanged`購読者へ通知してはならず、`CurrentSnapshot`の更新順と`SnapshotChanged`の通知順を同じpublish順で単調にする。

- snapshotのstate更新とpublish順の確定は不可分に行い、通知はその順序を維持して直列化する。外部event handlerを内部state lock保持中に呼び出すことは要件とせず、lock外で通知順を保証できるpublish直列化手段を用いてよい。

## 9.4 レート制限

Retry-Afterの秒数/日時、GoogleのrateLimitExceeded、Graphの429等をProviderError.RetryAfterへ正規化する。待機中にSemaphoreSlimの枠を保持せず、AccountSyncStateをRateLimitedUntilへ更新して指定時刻に1回再キューする。新しい制限応答が来たら時刻を更新する。手動更新も指定時刻を迂回しない。

再キューの対象単位はカレンダーとする。指定時刻に再実行する内部要求は、その時点でRateLimited状態が残る表示対象CalendarId集合だけを対象とし、それ以外のカレンダーは再取得せず結果、cache、CalendarSyncStateを維持する。対象集合が空の場合は再実行しない。集約規則は9.3と同じく和集合とする。

## 9.5 キャンセルとタイムアウト

- ルート終了、アカウント削除/無効化、カレンダーOFF、認証操作キャンセルのtokenをリンクする。

- TimeoutによるOperationCanceledExceptionとユーザー/終了キャンセルを区別する。前者はTransientTimeout、後者はCancelledでリトライしない。

- Delay、Semaphore待機、SDKページング、ファイルI/O、Dispatcher待機の全てにCancellationTokenを渡す。

## 9.6 最終更新時刻

AccountSyncState.LastFullySuccessfulSyncUtcは、そのアカウントの全表示対象カレンダーのCalendarSyncStateがSucceededになった時点で更新し、値はそれらCalendarSyncState.LastSuccessUtcの最小値とする。1回の同期で全カレンダーが成功する必要はなく、Startup retryのように複数回の同期にまたがって全カレンダーが揃った場合も更新する。最小値を採ることで、先行同期で取得した古い側の鮮度を上限として扱い、鮮度を過大表示しない。

表示対象カレンダーが1件でもSucceeded以外であれば更新せず、既存値を保持する。同期エラー中、レート制限待機中、キャンセル時も既存値を保持する。表示対象カレンダー0件のアカウントは同期対象外であり、既存値をそのまま維持する。

メインの最終更新は、表示対象アカウント全てのこの値の最小値をWindowsローカルへ変換したものとする。対象のいずれかが未成功なら『最終更新 --』とし、対象0件では値を表示しない。
