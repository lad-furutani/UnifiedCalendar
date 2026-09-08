# 10. 設定・キャッシュJSON、原子的保存、スキーマ移行

## 10.1 保存場所

```text
%LOCALAPPDATA%/UnifiedCalendar/
├─ settings/settings.json
├─ tokens/google/{internalAccountId}.json
├─ tokens/microsoft/{internalAccountId}.json
├─ cache/accounts/{internalAccountId}.json
├─ logs/unifiedcalendar-YYYYMMDD.log
└─ recovery/
```

正式製品名確定後にトップフォルダ名を変更する。AppPathsへ集約し、文字列を各Storeへ直書きしない。通常設定とキャッシュは平文であり、予定件名・説明・場所・URL・アカウント表示情報がWindowsユーザープロファイル内に残る。

## 10.2 settings.json v2

```text
{
  "schemaVersion": 2,
  "display": {
    "days": 7, "fontSizeDip": 14, "density": "standard",
    "defaultEventColor": "#2F6FED"
  },
  "sync": { "intervalMinutes": 5 },
  "general": { "startWithWindows": true },
  "notifications": { "enabled": true, "leadMinutes": 5 },
  "windows": {
    "main": { "leftDip": 0, "topDip": 0, "widthDip": 0,
      "heightDip": 0, "monitorDeviceName": "...",
      "savedDpiX": 144, "savedDpiY": 144 },
    "settings": { "leftDip": 0, "topDip": 0,
      "widthDip": 0, "heightDip": 0, "monitorDeviceName": "..." }
  },
  "accounts": [{
    "internalAccountId": "e7a3...", "provider": "google",
    "providerSubjectId": "opaque-id", "displayName": "...",
    "email": "...", "enabled": true,
    "tokenRef": "google/e7a3...",
    "calendars": [{ "calendarId": "...", "isVisible": true }]
  }],
  "colorRules": []
}
```

- JSONはUTF-8（BOMなし）、camelCase、enumはcamelCase文字列、日付時刻はUTCのISO 8601 round-trip、色は不透明#RRGGBB。

- fontSizeDipは10～24、daysは1～90、intervalMinutesは1/5/10/15/30/60、leadMinutesは1～60だけをvalidationで許可する。

- ウィンドウ未検証値は0で保存せずnull扱いにし、初期レイアウトへフォールバックする。

- **v2は1.1.0で追加した（2026-09-08 決定）。追加は`notifications`セクションだけである。** `SettingsSchemaV1ToV2Migration`がv1のファイルへ既定値（`enabled: true`、`leadMinutes: 5`）を補い`schemaVersion`を2にする。**v1をそのまま隔離してはならない**（アカウント登録とトークン参照が失われ、再認証が必要になる）。移行の登録先はDIと`SettingsJsonStore`の既定配列の2箇所である。v0のfixtureはv0→v1→v2と連鎖する。

- **1.1.0が保存したv2を1.0.0で読むと、10.5のnewer判定で退避され既定設定になる。** ダウングレードするとアカウント登録が失われる。既存仕様どおりの挙動であり変更しない。

## 10.3 account cache v1

```text
{
  "schemaVersion": 1,
  "internalAccountId": "e7a3...",
  "provider": "microsoft",
  "generatedAtUtc": "2026-08-27T03:10:00.0000000Z",
  "calendars": [{
    "calendarId": "...", "name": "...", "sourceColor": "#...",
    "lastSuccessfulSyncUtc": "...Z",
    "events": [ { "key": { "provider": "microsoft",
      "internalAccountId": "...", "calendarId": "...",
      "sourceEventId": "...", "occurrenceKey": "..." },
      "title": "...", "timing": { "kind": "timed",
      "startUtc": "...Z", "endUtc": "...Z" } } ]
  }]
}
```

## 10.4 原子的保存

1.  対象と同じディレクトリに.{filename}.{pid}.{guid}.tmpを作る。

2.  JsonSerializer.SerializeAsyncで全内容を書き、FileStream.FlushAsync後にFlush(flushToDisk: true)を呼ぶ。

3.  既存ファイルがあればFile.Replace(temp, target, null)、なければ同一ボリュームのFile.Moveを使う。

4.  失敗時は本体を残してtempを削除し、I/Oカテゴリだけをログへ記録する。パスごとのSemaphoreSlimと単一インスタンスで競合を防ぐ。

5.  起動時に残存tmpを安全に削除する。設定/キャッシュ/トークンで同じAtomicFileWriterを共有する。

## 10.5 スキーマ判定・移行・退避

| 入力 | Settings | Cache |
| --- | --- | --- |
| current | 検証後に読込 | 検証後に読込 |
| older | ISettingsMigrationを1版ずつ適用し、成功後にcurrentとして原子的保存 | recoveryへ退避して破棄、最新同期 |
| newer | future-v{n}-{timestamp}.jsonへ退避、既定設定 | future-v{n}-{timestamp}.jsonへ退避、最新同期 |
| corrupt/invalid | corrupt-{timestamp}.jsonへ退避、既定設定 | corrupt-{timestamp}.jsonへ退避、最新同期 |

退避は診断用であり有効世代ではない。各種・元ファイルごとに新しい3件を保持し、それより古い退避を削除する。設定マイグレーションはJsonNodeを使う純粋変換として単体テストし、途中失敗では元ファイルを変更しない。
