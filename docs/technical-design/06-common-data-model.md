# 6. 共通データモデル

## 6.1 CalendarEvent

CoreのCalendarEventは不変recordとし、Provider変換完了後に変更しない。WPF表示用値は別のPresentedEventへ投影する。

| フィールド | 型 | 規則 |
| --- | --- | --- |
| Key | EventKey | provider + internalAccountId + calendarId + sourceEventId + occurrenceKey。独自永続GUIDは使わない。 |
| Title | string | 空ならリソース化した無題表示へ投影。ログへ出さない。 |
| DescriptionPlainText | string? | ProviderでHTML装飾を除去・Entity decode。詳細で選択/コピー可。 |
| Location | string? | 元値。URL検出は表示投影で行う。 |
| Timing | EventTiming | 時刻指定はUTC範囲、終日はDateOnlyの開始日と排他的終了日。 |
| SourceTimeZoneId | string? | 元サービスのTZ識別子。比較は正規化UTC、表示はWindowsローカル。 |
| ResponseStatus | AttendeeResponse | Accepted/Tentative/NotResponded/Declined/Unknown。 |
| SourceEventColor | RgbColor? | イベント固有色またはカテゴリ色。 |
| SourceCalendarColor | RgbColor? | カレンダー色。Provider内優先判定用。 |
| MeetingUri | Uri? | http/httpsのみ。会議アイコンの独立操作に使用。 |
| SourceDetailUri | Uri? | 元予定を開く絶対http/https URI。 |
| IsCancelled | bool | trueはProviderまたはPresentation入口で必ず除外。 |
| CalendarName | string | 詳細・ルール評価・オフライン復元用。 |

## 6.2 時刻モデル

```text
public abstract record EventTiming;
public sealed record TimedEventTiming(
    DateTimeOffset StartUtc, DateTimeOffset EndUtc) : EventTiming;
public sealed record AllDayEventTiming(
    DateOnly StartDate, DateOnly EndDateExclusive) : EventTiming;
```

- TimedはProviderでUTCへ正規化し、EndUtc < StartUtcは不正データとしてその予定だけ除外・診断ログ化する。

- AllDayは時刻へ変換せずDateOnlyのまま保持する。表示上の最終日はEndDateExclusive.AddDays(-1)。

- WindowsのローカルTZ変更通知でTimeZoneInfo.ClearCachedDataを呼び、Presentation全体を再生成する。

## 6.3 アカウント・カレンダー・同期状態

| モデル | 主要項目 | 備考 |
| --- | --- | --- |
| CalendarAccount | InternalAccountId(Guid), ProviderKind, ProviderSubjectId, DisplayName, Email, Enabled, TokenRef | Guidはアカウント登録時に生成。メールはUI用でログ禁止。 |
| CalendarDescriptor | CalendarId, ProviderLocator, Name, IsPrimary, CanReadEvents, SourceColor | MicrosoftのProviderLocatorは`/me/calendars`が返すcalendar IDを基にした不透明な参照情報。共有元メールボックスや`/users/{owner}` routeを表現しない。 |
| CalendarSelection | InternalAccountId, CalendarId, IsVisible | 新規登録時はPrimaryのみON。全OFFも許可。 |
| CalendarSyncState | Status, LastAttemptUtc, LastSuccessUtc, RetryAtUtc, ErrorCategory | 成功/失敗をカレンダー単位で保持。 |
| AccountSyncState | Status, LastAttemptUtc, LastFullySuccessfulSyncUtc, CalendarStates | 1件でも失敗ならアカウント警告。全OFFはメイン警告対象外。 |
| SyncSnapshot | Accounts, IReadOnlyList<CalendarEvent>, CapturedAtUtc | ImmutableArray等の読み取り専用集合。 |

表示対象カレンダーが0件のアカウントは同期対象外とする。境界上`CalendarStates`が空の`AccountSyncState`を返す必要がある場合、その`Status`は`NotStarted`とし、空集合に対する`All(Cancelled)`を理由に`Cancelled`としてはならない。`Succeeded`は1件以上の表示対象カレンダーが成功した場合、`Cancelled`は実際にキャンセルされた表示対象カレンダーが1件以上ある場合に限る。0件では既存cacheと`LastFullySuccessfulSyncUtc`を維持し、メイン警告を出さない。

Microsoft Graphの`ownership`、`canEdit`、`canShare`は、共有/non-owned calendarを`/me/calendars`から取得できることを実サービスsmokeで確認するための診断情報であり、初期版の製品モデル保持要件ではない。`CalendarDescriptor`およびProviderLocatorへ追加せず、Google Provider等へMicrosoft固有の共通概念を強制しない。初期版の製品動作には既存のCalendarId、ProviderLocator、Name、IsPrimary、CanReadEvents、SourceColorを使用する。

## 6.4 安定ID

EventKeyは長さ付きで正規化した各構成要素を保持するrecord structとし、Dictionaryキー比較に用いる。永続JSONでは構成要素を保持し、UIキー文字列は同じ構成要素のSHA-256 Base64Urlから算出してよい。これにより区切り文字衝突を避け、ログへ元イベントIDを出さない。

- Google単発: sourceEventId=event.id、occurrenceKey=空。繰り返し回: recurringEventId + originalStartTime（終日は日付）をoccurrenceKeyへ入れる。

- Microsoft単発: Prefer: IdType=ImmutableIdで得たevent.id。繰り返し回/例外: seriesMasterIdまたはiCalUIdとoriginalStart UTCをoccurrenceKeyへ入れる。

- 同一IDは既存EventRowViewModel.UpdateFromで値だけ更新する。IDが変わった場合は削除+追加として扱う。
