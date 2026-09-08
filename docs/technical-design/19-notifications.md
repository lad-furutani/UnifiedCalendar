# 19. 予定開始前の通知

**本章は1.1.0で追加した（2026-09-08 決定 D127〜D136）。** 1.0.0では通知を実装していない。基準仕様書は13.3（NTF-001〜NTF-010）、SET-021 / SET-022、ERR-013、WIN-015、ACC-013を正とする。

## 19.1 範囲

- **出すのは時刻付き予定の開始前通知だけである。** 同期エラー、レート制限、予定終了、トレイアイコンの変更は引き続き行わない（ERR-013）。

- **参照専用を崩さない。** 通知から予定を操作する導線を作らない。クリック動作、スヌーズ、通知履歴を持たない（NTF-009）。

- アカウント別・カレンダー別の設定は持たない。ON/OFFと分数の2つだけをユーザー設定とする。

- 表示方式はタスクトレイのバルーン通知（`NotifyIcon.ShowBalloonTip`）とする。**新規NuGetパッケージを追加しない**という制約と、OSの通知センターへ載るという利点の両方を満たす方式がこれである。カスタムのトースト用Windowを自作する案は、位置・DPI・多モニタ・複数同時表示・自動クローズをすべて自前で持つことになるため採らない。

## 19.2 設定モデルと永続化

- `NotificationPreferences(bool Enabled = true, int LeadMinutes = 5)`（`Core/Persistence`）。`LeadMinutes`は1〜60。範囲外は`ArgumentOutOfRangeException`。

- `AppSettings`へ`Notifications`を追加する。**引数は既存の並びの末尾へ足す**（位置指定の呼び出しが多数あるため）。

- **既定はON・5分前とする。** 1.0.0から更新した利用者は、設定を触らなくても通知が出始める。要望された機能であり、意図した挙動である。

- settings.jsonはv2へ上げる（10.2）。`notifications`セクションだけの追加であり、`SettingsSchemaV1ToV2Migration`が既定値を補う。**v1を隔離してはならない。** 隔離するとアカウント登録とトークン参照が失われる。

- SET-019（初期設定へ戻す）の対象に通知設定を含める。SET-020の除外項目は変えない。

## 19.3 判定ロジック

判定は`Core/Notifications`の純粋なplannerに置く。**`nowLocal`を引数で受け取り、`TimeProvider`を内部に持たない**（決定的にテストするため）。

- 入力は、投影済みの予定（`PresentedEvent`相当）、`nowLocal`、`LeadMinutes`、既通知キー集合。

- 通知する条件は次の2つを同時に満たすこと。

  1. `LocalStart - LeadMinutes分 <= nowLocal < LocalStart`（通知時刻を過ぎ、まだ開始していない）
  2. キーが既通知集合に無い

- **キーは`StableId`と`LocalStart`の組**とする。開始時刻が変われば新しいキーになり、開始前であれば再通知される（NTF-005）。

- 対象は`IsAllDay == false`かつ`LocalStart != null`。終日予定は対象外、ゼロ時間予定は対象、時刻付きの複数日予定は開始日時で1回（NTF-001）。

- **抑制の初期化（NTF-006）。** 起動後の最初の判定、`Enabled`のfalse→true、`LeadMinutes`の変更の3つでは、その時点で条件1を満たすものを既通知として登録し、通知しない。**アカウント追加やカレンダーONで新しく対象へ入った予定は初期化の対象にしない**（通常判定で通知される）。

- **集約（NTF-007）。** 同じ判定で2件以上該当した場合は、開始が最も早い1件を代表にして残りを件数で示し、該当した全件を既通知として登録する。バルーンは同時に1つしか出せず、続けて呼ぶと前が置き換わるためである。

- 既通知集合は**プロセス内だけ**で保持し保存しない。表示対象から消えたキーは取り除く（メモリの増加を防ぐ）。

- `Enabled`がfalseの間は判定も通知も行わず、既通知集合をクリアする。

## 19.4 表示

- `ITrayIconAdapter`へ`ShowBalloonTip(int timeoutMilliseconds, string title, string text)`を追加する。実装は`ToolTipIcon.Info`を使う。**`BalloonTipText` / `BalloonTipTitle` / `Icon` / `PlaySound`は引き続き露出させない**（プロパティ経由の状態保持とアイコン差し替えを禁じたままにする）。

- 呼び出しは必ずUIスレッドから行う（`IUiDispatcher`）。`NotifyIcon`はUIスレッドで生成されている。

- 文面の組み立てはApp側（`ITrayNotifier`の実装）で`IUiTextService`を使って行う。plannerは開始時刻・件名・件数を返すだけにする。

| リソースキー | 値 |
| --- | --- |
| `Notification.Title` | `まもなく開始` |
| `Notification.Body` | `{0:HH:mm} {1}` |
| `Notification.BodyMore` | `{0:HH:mm} {1} ほか{2}件` |

- **本文は200文字で切り省略記号を付ける。** Windowsのバルーンは本文256文字・タイトル64文字が上限である。

- `timeoutMilliseconds`は近年のWindowsではOSが無視する。固定値（10000）を渡し、設定項目にしない。

- **ログに件名・説明・場所・URLを出さない**（LOG-005、14章）。記録する場合は件数と結果だけにする。

## 19.5 コンポーネント配置

| 層 | 型 | 役割 |
| --- | --- | --- |
| Core | `NotificationPreferences` | 設定値と範囲検証 |
| Core | `EventNotificationPlanner` | 判定の純粋ロジック。`nowLocal`を引数で受ける |
| Infrastructure | `NotificationsJsonDocument` / `SettingsSchemaV1ToV2Migration` | 永続化と移行 |
| App / Shell | `ITrayNotifier` と実装 | 文面の組み立てとバルーン表示 |
| App / Shell | `EventNotificationHostedService` | 分境界の購読、投影、plannerの呼び出し |
| App / ViewModels | `NotificationSettingsViewModel` | 設定カテゴリ『通知』 |

- **`InternalRefreshSignal.RefreshRequested`（分境界）へ相乗りし、新しいタイマーを作らない**（NTF-003）。`MinuteRefreshHostedService`が`TimeProvider`基準で分境界を刻んでいる。

- 投影は`EventNotificationHostedService`が`CalendarPresentationService.BuildSnapshot`を自前で呼ぶ。`DisplaySettings`は利用者の表示日数設定をそのまま使う。**`MainWindowViewModel`に通知の責務を持たせない**（UI層に置くとウィンドウ非表示中の挙動とテストが苦しくなる）。1分あたり投影が二重に走ることは承知の上であり、1,000件基準に対する所要時間を計測して判断する。

- 投影はバックグラウンドで行い、表示だけUIスレッドへ戻す。UIスレッドをブロックしない。

## 19.6 テスト

- **plannerの単体テストで境界を明示する。** 通知時刻ちょうど（通知する）、開始ちょうど（通知しない）、1分前、1分後、終日除外、複数日、ゼロ時間、キー変化での再通知、集約、抑制の初期化、集合の掃除。

- `Phase7ShellTests`の`ITrayIconAdapter`契約テストは消さずに反転する。`ShowBalloonTip`の禁止だけを外す。

- スキーマは、**アカウント1件と色分けルール1件を含むv1のfixture**が隔離されず移行されることを検証する。空の設定では隔離の危険を検出できない。v0→v1→v2の連鎖とv3の退避も検証する。

- HostedServiceは、フェイクの`ITrayNotifier` / `IUiDispatcher` / `InternalRefreshSignal`で検証する。**待つ条件と検証する条件を一致させる**（D111〜D116の再発防止）。

- 件名fixtureがログへ出ないことを、16章のPII非出力テストと同じ作法で検証する。

## 19.7 既知の制約

- **集中モード（Focus Assist）やWindowsの通知設定で抑制されうる。** アプリ側から抑制を解除しない。出たか出なかったかをアプリは知れない（`ShowBalloonTip`は結果を返さない）。

- **サウンドはOSの既定に従う。** 無音指定も設定項目も設けない。

- **スリープや終了で通知時刻を過ぎた分は出ない**（NTF-004）。後追いの通知は仕様として持たない。

- **通知済みの記録は保存しない。** 再起動後、まだ開始前の予定は再度通知されることがある。
