# 17. Codex向け実装フェーズ分割とDefinition of Done

> **進め方**  各PhaseはRelease buildと対象testが成功した状態でcommit可能にする。UI値TBDを除き、未実装をTODOコメントで先送りしない。

## Phase 0 — Solution bootstrap

ソリューション、5製品project + 1 test project、Directory.Build.propsの共通build設定、Central Package Management、Generic Host、Serilog、CI相当のlocal build scriptを作る。

### Definition of Done

- net8.0-windows / x64で全projectがbuildする。

- App.xamlにStartupUriを置かずHost composition rootから空MainWindowが起動する。

- Directory.Build.propsでtarget/publish設定が一元化され、Directory.Packages.propsでpackage versionが固定される。

- TimeProvider.SystemがDIされ、DateTime.Now直接使用を検出するreview ruleをdocsへ記載する。

## Phase 1 — Core model and presentation rules

CalendarEvent、EventTiming、EventKey、Account/Calendar/SyncState、色・日付・ソート・表示境界・安定IDを実装する。

### Definition of Done

- Provider/WPF非依存のCoreが単体buildする。

- ACC-002/003/004のunit testとゼロ時間、終日tie-break、参加状態、色優先testが成功する。

- TimeProviderを固定して全時刻testが実時間に依存しない。

## Phase 2 — Persistence and protected tokens

AppPaths、AtomicFileWriter、ISettingsStore、ICacheStore、v1 schema、migration、quarantine、DpapiTokenStoreを実装する。

### Definition of Done

- settings/cache/tokenのround-tripとatomic fault injection testが成功する。

- older/current/newer/corruptの全分岐をtestする。

- token fileの平文検索でrefresh/access tokenが見つからない。

- ACC-007相当が自動testで成功する。

## Phase 3 — Google Provider

Google OAuth、CalendarList、Events全paging、繰り返し/色/参加状態/URL/説明変換を実装する。

### Definition of Done

- Google SDK型がCore/App public APIへ露出しない。

- fake HTTP/fixtureでpaging・cancelled・occurrence key・event color優先が成功する。

- Testing consent screenの実アカウントでPrimary + 追加/共有calendarの列挙とread-only同期を確認する。

## Phase 4 — Microsoft Provider

MSAL system browser、DPAPI token cache、Graph v1.0、`/me/calendars`列挙、`/me/calendars/{calendar-id}/calendarView`全paging、`Calendars.Read` + `MailboxSettings.Read`によるmasterCategories参照を実装する。

### Definition of Done

- Graph SDK/MSAL型がCore/App public APIへ露出しない。

- 組織/学校と個人Microsoftアカウント各1件で認証できる。

- 所有calendarに加え、認証ユーザーのOutlook予定表一覧へ追加済みで`/me/calendars`から列挙できる共有calendarについて、calendarView、occurrence、category color、webLink/meeting URLを確認する。

- 正式scopeでのsilent acquisition/refresh、consent_required/interaction_requiredの非破損扱い、再認証identity固定、tokenRefあたり1 identity、identity不一致・cancel・failure時の既存token保持、transient I/O非隔離、masterCategories失敗時のcalendar color fallbackを確認する。

- `ownership`、`canEdit`、`canShare`は共有/non-owned calendar取得を確認する実サービスsmokeの診断情報とし、CalendarDescriptorを拡張するDoDには含めない。

- 未リリースの`Calendars.Read.Shared`検証時に作成した開発用MSAL cacheから正式scopeへのproduction migrationはDoDに含めない。scope互換性の起点は`Calendars.Read` + `MailboxSettings.Read`とする。

- `/users/{owner}/...`による共有元メールボックス直接参照と、Outlook予定表一覧へ未追加の共有・委任calendarを初期版の実装・テスト対象へ含めない。

- 正式UnifiedCalendar App Registrationを`Calendars.Read` + `MailboxSettings.Read`へ合わせ、正式client IDで`/me/calendars`、共有calendarView、masterCategories、silent acquisition/refresh、PII/secret監査の最終smokeを完了してからPhase 4をPassとする。

- 通常の最終smokeは製品の`CalendarsReadScope`を使用する。旧`Calendars.Read.Shared`診断を残す場合はGit管理外smoke host内のlegacy/diagnostic専用定数へ分離し、製品のMicrosoftProviderOptionsへ旧scope定数を互換目的で復活させない。

- beta endpointとwrite scopeを依存/設定から検出しない。

### 完了記録（2026-08-31）

- **Status: Pass / Complete**。コードレビューとautomated testsはPassした。

- 正式`UnifiedCalendar` App Registrationと正式client IDを用い、個人Microsoftアカウント、共有calendar、管理者同意後の組織Microsoft 365アカウントで最終実サービスsmokeがPassした。

- DPAPI保存、別プロセスsilent acquisition、access token refreshとTokenStore再保存、Graph再同期、PII/secret log auditは個人・組織アカウントともPassした。

- 実データになかったtimed event、recurrence、meeting URL、category付き共有予定、複数ページdatasetはautomated testsで補完した。

## Phase 5 — Sync orchestration

CalendarSyncService、4並列、通常retry、rate limit defer、startup retry、partial merge、scheduler、minute refreshを実装する。

### Definition of Done

- 30秒、2/5/10秒、30秒×3、並列4をfake time/handlerで検証する。

- account/calendar partial success、all fail no cache write、shutdown cancel no staged commitが成功する。

- 重複triggerが完了後最大1回に集約される。

- ACC-005/006のservice testが成功する。

- startup retryが表示対象カレンダー単位で動作し、初回成功カレンダーを再取得も悪化もさせないことを検証する。

- 並行アカウント同期でsnapshot通知順が単調であり、古いsnapshotが後から通知されないことを検証する。

- LastFullySuccessfulSyncUtcが全表示対象カレンダー成功時のみ最小LastSuccessUtcで更新され、部分成功・失敗・キャンセル時に既存値を保持することを検証する。

- 対象CalendarId集合の異なる重複triggerが和集合として1回に集約されることを検証する。

## Phase 6 — Main WPF experience

MainWindow、status、virtualized timeline、sticky header、event row、details popup、差分適用、keyboardを実装する。

### Definition of Done

- 1,000件で仮想化され、主要操作に体感的引っ掛かりがない。

- 同期反映でReset/一瞬空を使わず、scroll/focus/detail IDを維持する。

- 一覧/詳細/empty/loading/unregistered/errorの仕様文言と操作が一致する。

- unregistered状態のアカウント追加とメイン警告からの再認証は、Phase 6では表示、文言、フォーカス、コマンド発行までを実装する。対話認証と設定書き込みの実行はApp層のinterface境界へ委譲し、Phase 7で実装する。Phase 6はその境界がテストdoubleで検証できることをもってDoDとする。

- 配色はライト/ダーク両対応のテーマ対応リソースで定義し、起動時のOSテーマを反映する。実行中のテーマ変更検知と即時反映の検証はPhase 7で行う。

- ACC-003/008/012の実機確認が通る。

## Phase 7 — Settings, accounts, color rules, shell

規模と作業性質が異なるため、Phase 7 は 7a〜7d へ分割する。7a は他から独立しており先行できる。7b が土台となり、7c と 7d はその上に乗る。7c と 7d は互いに独立で順序を入れ替えてよい。

### Phase 7a — Window shell, tray, single instance, DPI

メインウィンドウの位置・サイズ保存とDPI追従、タスクトレイ、単一インスタンス、自動起動、実行中のテーマ追従を実装する。設定画面は含まない。

#### Definition of Done

- `WindowState=Normal` の `RestoreBounds` をWPF DIPで保存し、`monitorDeviceName` と保存時DPIを併記して復元する。保存モニタがなければprimary work areaの右端へフォールバックし、画面外は操作可能範囲へclampする。最小化状態そのものは復元しない。

- `app.manifest` でPerMonitorV2を宣言し、100/150/200%のモニタ移動と`WM_DPICHANGED`で行MinHeight、時刻欄幅、Popupを再計算する。

- ×で非表示、最小化でタスクバー、完全終了はトレイの終了のみ。最小化時に詳細Popupを閉じ、復元しても自動再表示しない。トレイ格納前の一覧スクロール位置を再表示時に維持する。

- トレイメニューは表示/非表示、今すぐ更新、設定、終了の4項目。ダブルクリックは常に表示・前面化しトグルにしない。**「設定」はApp層のinterface境界とし、Phase 7bで実装する。**

- 非表示中も外部同期、1分内部更新、終了済み除外、進行判定、キャッシュ更新を継続し、通知・バルーン・音・トレイアイコン変更を行わない。

- Mutex `Local\UnifiedCalendar.{CurrentUserSid}` と Named Pipe `UnifiedCalendar.Activation.{CurrentUserSid}` で単一インスタンスとする。第二プロセスはactivateを送信して終了し、常駐しない。Pipe ACLはCurrentUserのみ。

- 自動起動を `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` へ引用した実行ファイルパスで登録する。初期ON。`IStartupRegistrationService` で即時反映する。**設定UIはPhase 7b。**

- 実行中のテーマ変更を検知し、Phase 6で定義したテーマ対応リソースを即時再評価する。

- ACC-009 / ACC-010 が通る。

### Phase 7b — Settings window shell and lightweight categories

モードレス設定画面、6カテゴリの枠、確定操作規則、設定ウィンドウの位置・サイズ保存、表示・更新・一般カテゴリを実装する。

DoD は変更しないが、レビュー粒度のため納品を次の3回に分ける。

| 納品 | 内容 | 状態 |
| --- | --- | --- |
| 7b-1 | 設定更新の直列化、配置計算の汎用化、モードレス Window、6カテゴリの枠、確定操作の基盤 | 完了（合格） |
| 7b-2 | 表示カテゴリ、表示日数の同期要求、自動更新間隔の張り替え、および 7b-1 レビューの清掃項目 | 完了（合格） |
| 7b-3 | 一般カテゴリ、初期設定へ戻す、タイムゾーン追従 | 完了（合格） |

7b-1 を分けたのは、Phase 7a で実機確認済みのウィンドウ配置経路へ手を入れるリファクタリングを、カテゴリの中身が乗る前に単独で確認するためである。さらに 7b-2 と 7b-3 へ分けたのは、残りが Phase 5（同期制御）、Phase 6（投影）、Phase 7a（DPI・レイアウト）の3つの受入済み領域をまたぐためである。また「初期設定へ戻す」は表示・更新・一般の各項目を戻す処理であり、それらが揃う前には SET-020 の除外項目の検証ができない。

#### Definition of Done

- OwnerをMainWindowとしたモードレスWindowを1つだけ保持し、再度開く操作は既存をActivateする。Owner非表示中も操作可能状態を維持する。

- OK/Apply/Cancel/×/終了時の未適用変更規則が通る。即時反映済みの変更はCancelや×で元に戻さない。

- **Phase 7b で扱う設定項目はすべて即時反映・即時保存とする。** SET-011（表示日数）、SET-013（密度）、SET-014（共通色）、SET-012（フォントサイズ）、SET-016 / WIN-016（自動起動）はいずれも仕様が即時反映を明示しており、SET-015 の自動更新間隔も同じ軽量項目として即時反映に含める。したがって 7b では未適用変更が発生しない。OKは閉じる、Applyは未適用変更が無い間は無効、Cancelは破棄対象が無いため閉じるだけ、×は確認なしで閉じる。未適用変更の枠組み（IsDirty、破棄確認、UI-018の終了時確認）は Phase 7c / 7d のアカウント操作と色分けルールのために実装しておく。

- **表示日数を拡大した場合は同期を要求する。** SET-011 の「取得範囲へ即時反映」を満たすため、拡大時は再投影に加えて同期を要求する。縮小時は再投影のみとする。新しい `SyncTriggerReason` を追加せず既存の値で表現する。

- 設定ウィンドウの位置・サイズを保存・復元し、画面外・モニタ・DPI変更で操作可能な位置へ補正する。

- 表示（表示日数1〜90、フォントサイズ、密度、共通デフォルト色）、更新（自動更新間隔）、一般（自動起動、アプリ情報、ログ/設定フォルダを開く、初期設定へ戻す）が動作する。

- SET-009 の未反映状態がUIで識別できる。

- SET-019 の初期化が対象項目だけを戻し、SET-020 の除外項目（アカウント、認証情報、カレンダーON/OFF、ウィンドウ位置・サイズ）を維持する。

- Phase 7a でinterface境界としたトレイの「設定」からこのウィンドウを開く。

- アカウント、カレンダー選択、色分けルールの各カテゴリは枠のみとし、中身はPhase 7c / 7dで実装する。

#### 完了記録（2026-09-03）

- **Status: Pass / Complete。** 3回の納品（7b-1 / 7b-2 / 7b-3）と2回の修正（実機受入指摘）を経て、コードレビューと実機受入の双方をPassした。

- 実機受入は `docs/acceptance-phase7b.md`。確認項目64件のうち**59件合格、5件未実施**。未実施は F-5（同期中の間隔変更）、G-11（対象不在時のパス操作）、I-4（時刻のみ変更）、K-1（ACC-008体感評価）、K-2（ログオン時自動起動）であり、いずれも手作業では検証できないか Phase 8 への持ち越しである。**該当する挙動は自動テストで担保している。**

- 完了時点で automated tests **411件全緑**、Release build Warning 0 / Error 0。

- 実機受入で不具合4件を検出し、すべて修正して再確認した。うち3件はテーマ・フォント追従に関するもので、**「既定のコントロールテンプレートがアプリのテーマやフォント設定を知らない値を持っている」という同じ根本原因**であった（Phase 7a の同種指摘を含めると4回目）。通常状態だけでなく**選択・ハイライト・フォーカス等の状態依存の配色**まで閉じることで解消した。

- TBD-011（色デザイン）のパレット8色を確定し、`11-presentation-pipeline.md` 11.3 へ記載した。全色がWCAG AAを満たす。

- Phase 7b で扱う設定項目はすべて即時反映・即時保存であり、未適用変更は発生しない。未適用変更の枠組み（IsDirty、破棄確認、UI-018の終了時確認）はテスト注入で検証済みで、Phase 7c / 7d のアカウント操作と色分けルールが利用できる。

### Phase 7c — Account and calendar management

対話認証、アカウント一覧と操作、カレンダー選択を実装する。

#### Definition of Done

- Phase 6 でinterface境界とした `IAccountRegistrationService` / `IAccountReauthenticationService` を実装し、未登録画面とメイン警告の導線を有効化する。

- ACM-001〜010 のアカウント操作（複数登録、重複禁止、一覧項目、並び順、追加ボタン、初回同期状態、再認証、有効/無効、削除と確認ダイアログ）が通る。

- ACM-005 の追加後フローで、認証完了後にカレンダー一覧を続けて表示し、ON/OFFを設定してからアカウント一覧へ戻る。設定画面は待たずに閉じられる。

- ACM-013 のとおり新規アカウントはメインカレンダーだけON、他をOFFとする。

- ACM-011〜021 のカレンダー選択（一覧項目、長い名称の省略とホバー、画面を開いた時点の再取得、取得不可の理由表示、OFF/ONの即時反映、全OFFアカウントの扱い、Microsoft共有カレンダーの案内）が通る。

- ACM-009 の削除で認証情報、アカウント設定、カレンダー選択、専用キャッシュを全て削除し、他アカウントへ影響しない。

- ERR-003 の設定画面カレンダー警告と ERR-004 の部分復旧が動作する。

- ACC-001 の実機確認が通る。

DoD は変更しないが、レビュー粒度のため納品を2回に分ける。

| 納品 | 内容 | 状態 |
| --- | --- | --- |
| 7c-1 | 対話認証の配線、アカウント一覧と操作（ACM-001〜004 / ACM-006〜010）、追加時の初期カレンダー選択（ACM-013）、未登録画面とメイン警告の導線有効化 | 完了（合格） |
| 7c-2 | カレンダー選択（ACM-011〜012 / ACM-014〜021）、追加後フローでのカレンダー選択（ACM-005）、ERR-003 / ERR-004 | 完了（合格） |

分割の理由は2つある。**対話認証は実サービスに依存し、失敗時の切り分けが最も難しい**ため、ここを単独の納品にして「アカウントを1件追加すると予定が出る」まで通しで確認できる状態を先に作る。また、カレンダー選択は取得不可カレンダーの理由表示や再取得失敗時の扱いなど独立した検討事項が多く、アカウント操作と混ぜるとレビュー粒度が粗くなる。

#### Phase 7c で必要になる契約追加

実装着手前の調査で、既存契約のままでは満たせない要求が2件見つかった。いずれも Phase 7c-1 で追加する。

- **`IAccountRegistrationService.IsProviderAvailable(ProviderKind)`** — Google / Microsoft の各プロバイダは対応する ClientId が構成に存在するときだけ DI へ登録される（`ServiceCollectionExtensions.cs`）。単一の `IsAvailable` では ACM-004 の2つの追加ボタンを個別に制御できない。

- **`ICalendarSyncService.RefreshFromSettingsAsync(CancellationToken)`** — `CalendarSyncService.BuildSnapshotLocked()` は同期サービスが保持する設定を参照し、その設定は `InitializeAsync` と `RequestSyncAsync` でしか更新されない。ACM-008（無効化の即時非表示）と ACM-018（カレンダーOFFの即時非表示）は**ネットワークを伴わずに**反映する必要があるため、設定を読み直してキャッシュから snapshot を再構築するだけの入口を設ける。

#### 7c-1 完了記録（2026-09-06）

- **Status: Pass / Complete。** 1回の納品（`docs/handoff-phase7c-1.md`）と1回の修正（`docs/handoff-phase7c-1-remediation.md`）を経て、コードレビューと実機受入の双方をPassした。実機受入は `docs/acceptance-phase7c-1.md` で、**指摘なしで完了**した。

- **リポジトリで初めて実サービスの対話認証を production 経路で通した納品である。** Google / Microsoft の両方で、アカウント追加、初回同期、再認証、有効/無効、削除が実機で動作した。完了時点で automated tests 473件全緑、Release build Warning 0 / Error 0。

- プロバイダ実装と同期サービスは Phase 3〜5 の既存実装をそのまま使い、7c-1 は UI と設定書き込みへの配線に徹した。Core への追加は `IsProviderAvailable`、`RefreshFromSettingsAsync`、`AccountSyncTargetPolicy`、最終正常更新時刻の算出集約の4点で、**JSONスキーマは不変**である。

- **`CreateInitialState` が最終正常更新時刻を復元していなかった実装漏れを検出し、解消した。** 起動のたびにアカウント単位の `LastFullySuccessfulSyncUtc` が `null` になっており、同期対象外のアカウント（無効・全OFF）では永久に「未取得」のままだった。ACM-020 の「最終正常更新時刻は保持する」に対する Phase 5 からの不適合である。あわせて同期対象外のアカウントを「同期中」と表示していた問題も解消した。

- **キャンセルの扱いが Google と Microsoft で異なる。** 認証画面での利用者のキャンセルを、Google は `access_denied`（失敗メッセージを表示）、Microsoft は `authentication_canceled`（無表示）として返す。Google は「利用者の拒否」と「管理者によるブロック」を区別する手段が `access_denied` しかないため、失敗として扱う現状を許容した。

- **Phase 7d で追加したスタイル番人テストが機能した。** Phase 7a から5回続いていた配色不具合（既定のコントロールテンプレートがアプリのテーマを知らない色を持つ）は、設定画面に新しいカテゴリを1つ追加した本フェーズで**再発しなかった**。

- **Google のデスクトップ クライアントでは ClientSecret が必須であることが判明した。** ClientId だけでは認証が成立せず、PKCE を用いていても Google の token endpoint がシークレットを要求する。Microsoft（8.3）が client secret を作らない public client であるのと対照的である。詳細と、それが保護対象の秘密ではない理由は `08-oauth-and-token-storage.md` 8.6 へ記載した。

- 実機受入の準備を通じて、**ログのタイムスタンプが `14-logging.md` 14.1 の要求（ISO 8601 ローカルoffset付き）を満たしていない**ことが判明した。`CompactJsonFormatter` が CLEF 仕様により `@t` を必ずUTCで書くためで、ファイル名の日付（ローカル日）と中身の日付が食い違っていた。`docs/handoff-log-local-timestamp.md` で別途修正した（受入完了後）。

#### 7c-2 完了記録（2026-09-06）

- **Status: Pass / Complete。これで Phase 7c の Definition of Done が揃った。** 1回の納品（`docs/handoff-phase7c-2.md`）と1回の修正（`docs/handoff-phase7c-2-remediation.md`）を経て、コードレビューと実機受入の双方をPassした。実機受入は `docs/acceptance-phase7c-2.md`。完了時点で automated tests 490件全緑、Release build Warning 0 / Error 0。

- **実機受入は全項目パスした。** B章（再取得と取得失敗時の表示）は当初ネットワークを切る手段が無く未実施だったが、トークンファイル削除で取得を失敗させる手段に切り替えて後日実施し、3段フォールバックが実機でも動作することを確認した。

- カレンダー一覧の保持は**設定JSONのスキーマを変えずに**実現した。取得成功 → 実行中の直前成功（メモリ）→ 設定 + キャッシュ、の3段フォールバックとする。`SettingsJsonDocument.ToDomain` は `SchemaVersion` 不一致で例外を投げ、**移行処理が無いためファイルごと隔離される**ため、実アカウントが登録済みの状態でスキーマを動かす選択は採らなかった。

- **`CalendarId` を画面へ出さない制約を、型で担保した。** Google のメインカレンダーの `CalendarId` は利用者のメールアドレスそのものである。`CalendarListItemViewModel.CalendarId` を `internal` にすることで、WPF のバインドが public しか見ない性質により構造的に守られる。なお Google のメインカレンダーは `summary`（＝メールアドレス）が正式な名前として返るため、カレンダー名にメールアドレスが表示されるのは仕様どおりである。

- 取得失敗時のフォールバックでは `CanReadEvents` を `true` と仮定する。取得できていない以上、実際の可否は分からない。`false` にすると通信できない間はすべてのカレンダーが操作不能になり害が大きい。`true` なら実際には読めないカレンダーをONにしても、同期失敗が ERR-003 の警告として現れて回復できる。

- **ERR-004 の部分復旧は Core が Phase 5 から実装済みであり、7c-2 では UI へ写すだけで足りた。** `MergeAccountState` と `AccountSyncState.HasWarning` を再実装させない指示が有効に働いた。

- **スタイル番人テストが 7c-1 に続いて機能した。** 設定画面へ2つ目の新カテゴリを追加したが、Phase 7a から5回続いた配色不具合は再発しなかった。

### Phase 7d — Color rules

色分けルールの一覧、エディタ、優先順位編集、検証、プレビューを実装する。

DoD は変更しないが、レビュー粒度のため納品を2回に分ける。

| 納品 | 内容 | 状態 |
| --- | --- | --- |
| 7d-1 | ルールエディタ（CLR-005〜016）、検証（CLR-023）、キャンセル時の破棄確認（CLR-024）、一覧の最小形 | 完了（合格） |
| 7d-2 | 一覧の管理操作（CLR-017〜022）。条件概要と省略・ホバー、D&Dと上へ/下へ、有効/無効、削除確認 | 完了（合格） |

分割の理由は2つある。**D&D による並び替えはリポジトリに前例が無く**（`DragDrop` / `AllowDrop` の使用箇所ゼロ）、最も退行しやすい部分を独立させたい。また 7d-1 だけで「ルールを作ると予定の色が変わる」が通しで確認でき、CLR-001 の優先順位評価まで到達できる。

#### Definition of Done

- CLR-005〜016 のルール定義（名前必須・64文字・大文字小文字を含む重複不可、条件1件以上、件名/カレンダー名/取得元サービスの3条件種別、AND/OR、部分/完全一致、空値禁止、カレンダー名の候補選択と自由入力、固定パレットとPicker）が通る。

- CLR-017〜024 のルール管理（一覧項目、条件概要と省略・ホバー、D&Dと上へ/下へボタンの並び替え、有効/無効と薄い表示、削除確認、検証によるボタン無効化、キャンセル時の破棄確認）が通る。

- CLR-016 の固定サンプル予定行で背景色、自動文字色、進行中の進捗色を即時プレビューする。実予定データを使わない。

- 色rule優先、palette 8色、picker、contrast/previewが一致する。

- 保存後に一覧へ戻り、内容・優先順位・色が即時反映される。Coreの `ColorRules` 評価ロジックを再実装しない。

#### 完了記録（2026-09-03）

- **Status: Pass / Complete。** 2回の納品（7d-1 / 7d-2）と1回の修正（実機受入指摘）を経て、コードレビューと実機受入の双方をPassした。

- 実機受入は `docs/acceptance-phase7d.md`。確認項目45件が**すべて合格**。**機能面の不具合はゼロ**で、実機で検出したのは配色1件のみであった。**D&Dも合格**しており、自動テスト対象外だった唯一の確認機会を通過した。

- 完了時点で automated tests **435件全緑**、Release build Warning 0 / Error 0。

- Coreの `ColorRules` 評価ロジック、`CalendarPresentationService` の色決定、`ColorMetrics` の補正はいずれも再実装せず、既存実装をそのまま使った。Coreの変更は `ColorRule.MaximumNameLength` の公開1行のみで、JSONスキーマは不変である。

- CLR-016 の固定サンプル予定行を確定し、`11-presentation-pipeline.md` 11.3 へ記載した。**基準時刻を10:30に固定**することで、利用者が開いた時間帯によって進捗色が見えなくなる事故を防いでいる。

- Phase 7b-1 で用意した未適用変更の枠組み（`IPendingSettingsChanges`）が、Phase 7d で初めてproductionで使われた。ルールエディタの未保存差分だけがdirtyとなり、Phase 7b の即時反映項目はdirtyにしない。

- **実機受入で検出した配色不具合は、Phase 7a から数えて同じ根本原因の5回目であった**（既定のコントロールテンプレートがアプリのテーマを知らない色を持つ）。個別修正に加え、**設定画面に現れるコントロール型のうちスタイル未定義のものがあれば失敗する番人テストを追加**し、Phase 7c 以降の再発を防ぐ仕組みとした。

## Phase 8 — Release hardening and installer

全受入基準、privacy review、Inno Setup、Release publish、運用導線を仕上げる。

### Definition of Done

- ACC-001～012の証跡が揃う。

- ログにメール、件名、説明、場所、URL、tokenが含まれない。

- win-x64 self-contained installerで新規install/upgrade/uninstallを確認する。

- 初期版対象外のUI/処理/API scopeが混入していない。

- 正式版product decisionが反映され、Version=1.0.0でRelease buildが再現できる。

### 第0段の決定記録（2026-09-06）

Phase 8 の着手前に、持ち越しのうち判断を要する事項を決定した。検討内容は `docs/phase8-decisions.md` にある。

- ~~**Google OAuth 同意画面は「テスト」のまま出荷する。**~~ **2026-09-08 に「本番環境（未検証）」へ変更した。** 出荷時はテストのままとしたが、7日ごとの再認証とテストユーザーの個別登録が常駐アプリの性質に対して重すぎたため切り替えた。**審査は通していない。** 切り替えにはブランディングページの必須4項目（アプリ名・サポートメール・ホームページURL・プライバシーポリシーURL）と承認済みドメインの登録が必要で、**「トグルだけ」ではなかった。** 経緯と残る制約（警告画面、同意済みユーザー数の上限）は `08-oauth-and-token-storage.md` 8.6。

- **ACM-017 の後段は仕様文を実態へ合わせた。** 「通常の予定同期ごとには再取得しない」を「予定同期は、表示中のカレンダー一覧を書き換えない」へ改めた。同期側で descriptor を保持する形へ直す案は、Phase 5 の受入済み挙動（部分成功のマージ、リトライ、レート制限の繰り延べ）への退行リスクに対して得るものが API 呼び出し1回の削減にとどまるため採らない。

- **進行中の予定の進捗色が WCAG AA を下回る件は、既知の制限として判断のうえ出荷する。** 進捗部分は装飾であって情報の担い手ではなく、件名・時刻は進捗部分の外側でも読める。理由と代替案を採らない根拠、将来見直す条件を `11-presentation-pipeline.md` 11.3 へ記載した。

- **仕様書の TBD 表を棚卸しした。** TBD-002 / 003 / 005〜010 / 012〜017 / 019 / 020 の15件を、決定内容と出所を明記してクローズした。**TBD-020 は仕様書の記述（「初期版は平文で確定」）が実装（DPAPI CurrentUser）と矛盾していたため訂正した。**

- **製品識別・アイコン・配布表示を決定した（2026-09-06）。** 正式製品名・exe名・`%LOCALAPPDATA%` のフォルダ名はいずれも `UnifiedCalendar` のままとし、変更しない（`AppIdentity` の定数をそのまま用いる。変更が無いため既存の設定・トークンが引き継がれる）。正式アイコンは 16/24/32/48/64/96/128/256px の8サイズを 32bpp で含む単一の `.ico` を受領した。Publisher 名は `Logic and Design Inc.`、Setup 表示名はすべて `UnifiedCalendar`（和名を併記しない）。コード署名は導入済みの証明書を Inno Setup の `SignTool=MySignTool` で用いる。**`MySignTool` は Inno Setup IDE 側に登録されたマシンローカルの定義へ解決されるため、リポジトリには入らない。** 資格情報（8.7）と同じく、リポジトリは参照だけを持つ。詳細は 18章 P-001 / P-002 / P-005。

### 第2段着手前の決定記録（2026-09-07）

インストーラの設計にあたり、次を決定した。詳細は 18章 P-005 と `docs/handoff-phase8-3-installer.md`。

- **インストール範囲はユーザー単位（`PrivilegesRequired=lowest`）とする。** データ・トークン・自動起動がすべてユーザー単位に閉じているため。マシン単位にすると、昇格したアンインストーラが別アカウントとして動きうるため**ログオン中ユーザーの HKCU 自動起動エントリを確実に消せず**、存在しない exe を指す自動起動が残る。

- **アンインストールでユーザーデータは削除しない。** `%LOCALAPPDATA%\UnifiedCalendar` を残し、入れ直せば設定とアカウント登録が戻るようにする。**HKCU の自動起動値だけは削除する。**

- **インストーラのオプションは「インストール後に起動」のみ。** 自動起動は設定画面（H-5）が唯一の管理者であり、インストーラからも設定できるようにすると二重管理になる。

### 完了記録（2026-09-07）

**Phase 8 の Definition of Done をすべて満たした。持ち越し6件はすべて解消した。**

段階ごとの成果は次のとおり。

| 段 | 内容 | 指示書 |
| --- | --- | --- |
| 第1段 | クライアント資格情報の配布（ビルド時埋め込み＋環境変数上書き、解決順序と Google の組ルール、資格情報 record の秘匿） | `handoff-phase8-1-client-credentials.md`（D74〜D77）、`handoff-phase8-2-cleanup.md`（D82） |
| 第1段 | テストの安定化と重複解消（ディスパッチャ・ヘルパー集約、`ILocalTimeZoneProvider` / `ToCalendarAccount` の重複解消、保存失敗時のチェック表示） | `handoff-phase8-2-cleanup.md`（D78〜D83）、`handoff-phase8-2-remediation.md`（D84〜D86） |
| 第2段 | 製品識別情報、アイコンの適用、Inno Setup インストーラ、初期版対象外の混入監査 | `handoff-phase8-3-installer.md`（D87〜D90） |
| 第2段 | 同梱物の署名（`signonce`）とビルドスクリプトのオプション整理 | `handoff-phase8-3-remediation.md`（D91〜D93） |
| 第3段 | 実機受入。その過程でアンインストール時の実行中検出を追加 | `acceptance-phase8.md`、`handoff-phase8-uninstall-running-app.md`（D94 / D95） |

**Definition of Done への対応。**

- **ACC-001〜012 の証跡。** 新規に確認したのは ACC-008（体感評価）、ACC-009 の残り（メイン画面とポップアップのダークモード）、ACC-011（参照専用の目視）の3件。残り9件は過去の受入と自動テストで済んでおり、一覧は `acceptance-phase8.md` 付録A。
- **ログの PII。** 実機のログを実データで検索し、検出0件（`acceptance-phase8.md` G 章）。
- **installer の新規install / upgrade / uninstall。** すべて実機で確認（同 A / H / I 章）。**アンインストール時に実行中アプリを検出していない不具合を発見し、D94 / D95 で修正のうえ再確認した。**
- **初期版対象外の混入。** 18.1 の全項目と API scope をコード監査し、混入0件（`handoff-phase8-3-installer.md` D90 の20項目表）。
- **Version=1.0.0 の Release build。** 発行 exe のプロパティで確認。**ビルド時刻を埋め込むためバイト単位の再現性は無く、「同じ手順で同じバージョンの出荷物が作れる」という解釈で満たしたものとする。**

**自動テストは 504 件合格、Release build は Warning 0 / Error 0。**

**出荷前の仕上げ（2026-09-07、D96 / D97）。** インストーラの表示バージョンとファイル名を製品バージョンへ揃えた。`AppVersion` と `OutputBaseFilename` は `GetStringFileInfo(AppExePath, "ProductVersion")`（`1.0.0`）から、`VersionInfoVersion` は `GetVersionNumbersString`（`1.0.0.0`）から取る。**Windows のバージョンリソースは FileVersion が数値固定、ProductVersion が文字列という別の欄なので、両方を残すのが正しい。** 結果としてインストーラとアプリ exe の FileVersion / ProductVersion / ProductName / CompanyName / 署名者がすべて一致した。`build-installer.ps1` の期待ファイル名も `ProductVersion` へ合わせた（合わせないと存在しないパスを探して失敗する）。指示書は `handoff-phase8-installer-version-string.md`。

**この変更により `IncludeSourceRevisionInInformationalVersion=false`（D87）がインストーラのファイル名にとって不可欠になった。** 外すと `ProductVersion` が `1.0.0+<commit>` になる。**`InformationalVersion` を検証する既存テストがこれを守っている。**

**資格情報の入れ忘れへの歯止め（2026-09-07、D98 / D99）。** 出荷判断の直後、**実際に資格情報が空のインストーラを作ってしまう事故が起きた。** 実機受入のF章（ログオン時自動起動）でサインアウト／サインインを行ったため、資格情報を入れていたPowerShellセッションの環境変数が消え、そのままビルドした。**ビルド・全テスト・署名・インストール・アンインストールのすべてが成功する**ため、気づけたのは導入後にアカウント追加ボタンが不活性であることとログの1行だけだった。

対処として、`build.ps1` は渡されなかった資格情報を**出力の最後に警告する**（失敗はさせない。資格情報の無いマシンでビルドできることは 8.7 の決定）。Googleの片方だけが設定された状態は「設定したつもりで効いていない」ため、より強く警告する。`build-installer.ps1` は**資格情報の無い発行物に対して既定で失敗し**、`-AllowMissingCredentials` を明示したときだけ続行する。**判断は現在の環境変数ではなく、`build.ps1` が発行時に残した記録に基づく**（実行時の環境変数はビルド時の状態と一致せず、今回の事故がそのまま素通りするため）。記録は値を持たず有無だけを保持し、`artifacts\publish\win-x64` の外に置く（発行フォルダは `.iss` が丸ごと収めるため）。指示書は `handoff-phase8-credential-build-guard.md`。**8.7 の「ビルドスクリプト側での必須チェックは設けない」という判断はこれで撤回した。**

**歯止めは値の中身を見ない。** 有無しか判定しないので、ダミー値が入った発行物は素通りする。**最終的な確認は、導入してログの解決元が `Embedded` であることと、実際にアカウントを追加できることで行う。** 資格情報の確認場所は 8.8。

**第三者ライセンス表示の同梱（2026-09-07、D100〜D102）。** 同梱物のライセンスを調査したところ**すべて MIT または Apache-2.0** で、コピーレフトも再配布を制限する商用ライセンスも無かった（詳細は 18章 18.2）。ただし**どちらも著作権表示とライセンス本文の添付を求めており、これを1つも同梱していなかった。** `THIRD-PARTY-NOTICES.txt` を作成してインストーラで `{app}` へ同梱した。指示書は `handoff-phase8-third-party-notices.md`。

**この作業で、Work の事前調査が不完全だったことが判明した。** 指示書に載せた表は21パッケージだったが、`project.assets.json` の解決済み依存は**69パッケージ**あった。署名スキャンと既知の依存から表を組み立てたため、Microsoft 署名の中に埋もれた第三者パッケージ（`Microsoft.IdentityModel.*`、`Microsoft.Kiota.*`、`Microsoft.IO.RecyclableMemoryStream`、複数の `System.*`）を落としていた。**表どおりに書けば受入基準は満たせたが、48パッケージ分の表示が欠けたまま配布することになっていた。** 指示書の Open questions に「表を信じず実測を報告すること」と書いていたことで拾えた。

**ズレ検知は `project.assets.json` との照合で行う。** `UnifiedCalendar.App` の解決済みパッケージだけを見るため、テスト専用パッケージは**除外リストを持たずに構造的に外れる。** 推移的依存も拾うので、今回落とした種類の漏れが再発しない。

**1.0.0 の出荷可と判断した（2026-09-07）。** 署名済みインストーラで新規インストール・上書きインストール・アンインストールを実機で確認し、表示バージョン、アプリ一覧が二重に増えないこと、実行中アプリの検出を含めて問題が無かった。**未決の TBD・製品判断・持ち越しはいずれも残っていない**（仕様書 TBD 20件、18章 P-001〜P-005、本章の持ち越し6件がすべてクローズ）。

**実機受入の実施上の注意。** Work 側の環境からインストーラを実行してはならない。レジストリとファイルの書き込みがデスクトップセッションから分離されており、作った ARP エントリが設定アプリ・`appwiz.cpl`・`winget` のいずれからも見えなかった。**状態を変える操作は実施者が行い、Work は読み取りによる確認に留める。**

### 先行フェーズからの持ち越し

**6件すべて解消済み（2026-09-07）。**

| # | 内容 | 出所 |
| --- | --- | --- |
| 1 | ~~**ACC-008 の体感評価**~~。**完了。** Phase 6 でリモートデスクトップのため保留、Phase 7b / 7d でも未実施だったものを `acceptance-phase8.md` C 章で実施し合格。表示日数90日で実データを増やして確認した。**TBD-018 はこれで閉じる。** ただし件数と体感待ち時間の実数値は記録していない | `acceptance-phase7b.md` 付録B-5 |
| 2 | ~~**ログオン時自動起動（H-5）**~~。**完了。** サインアウト／サインインを伴うため3フェーズ持ち越されていたものを `acceptance-phase8.md` F 章で実施し合格。**インストール先が変わってもアプリが起動時にパスを修正することも確認した** | `acceptance-phase7a.md` 付録B-3 |
| 3 | ~~**間欠的なテスト失敗**~~。**完了（Phase 8-2 D84 / D85）。** 原因のテスト1件と同型の8件を是正し、修正後に `dotnet test -c Release` を Codex 20回・Work 22回繰り返して失敗0件を確認した。当初は単発 `PumpDispatcher()` が原因と見ていたが、Phase 8-2 のレビューで再現した1件（`MainWindowViewModelTests.InternalMinuteRefreshReprojectsWithoutCallingAProvider`、18回中2回）は `PumpDispatcher` を一切使っておらず、**`WaitUntilAsync` の待機条件がアサーション対象と異なる**ことが原因だった。原因の分類を「待つ条件と検証する条件の不一致」へ改める。**ただしこの「失敗0件」は実行順に依存した偶然であり、出荷後に同型の不具合が3種見つかった（後述「テストの『全件合格』が偶然だった件」、D111〜D116）。** | `acceptance-phase7d.md` 付録B-2、`handoff-phase8-2-remediation.md` D84 |
| 4 | ~~**レビューで【軽微】とした各件**~~。**完了。** `SystemLocalTimeZoneProvider` / `FixedLocalTimeZoneProvider` を Core へ集約、`ToCalendarAccount` を Core の拡張メソッドへ集約、保存失敗時のチェック表示を是正（Phase 8-2 D80 / D81 / D83）。`ToCalendarAccount` の引数チェックも追加（D86）。**据え置くと決めた6項目は 8-2 指示書の「やらないこと」に理由つきで記録済み** | 各 handoff のレビュー結果 |
| 5 | ~~**クライアント資格情報の配布方式の実装**~~。**Phase 8-1 で完了（2026-09-07）。** 以下は当初の記述。方式は 8.7 で決定済み（ビルド時にアセンブリへ埋め込み、環境変数で上書き。既定は空でビルド可能）。`build.ps1` からの値の受け渡し、解決順序と Google の ClientId/Secret 組ルールを閉じたクラス、解決元のログ出力、installer 経由での認証確認を実装する。Phase 8 の DoD「win-x64 self-contained installer で新規install/upgrade/uninstallを確認する」の前提条件になる | `08-oauth-and-token-storage.md` 8.7 |
| 6 | ~~**メイン画面とポップアップのダークモード実機確認**~~。**完了。** `acceptance-phase8.md` D 章で実施し合格。**詳細ポップアップの区切り線は実機で問題とならなかったため、決定どおり手を入れない。** トレイアイコンの16px表示も同章で確認した | `handoff-main-window-theme.md` |

## 1.0.0 出荷後の記録（2026-09-08）

出荷後に行った作業を記録する。**製品の機能は変えていない。**

| 作業 | 内容 | 指示書 |
| --- | --- | --- |
| ソース公開 | Apache-2.0 でソースのみを公開し、リポジトリを公開／非公開／凍結の3つへ分けた | `handoff-oss-publication.md`（D103〜D108）、`handoff-publication-script-encoding.md`（D109 / D110） |
| テストの安定化 | 実行順に依存していたテスト1件と、待機の不備3種を是正した | `handoff-test-isolation-theme-resource.md`（D111 / D112）、`handoff-test-wait-signal-mismatch.md`（D113 / D114）、`handoff-test-wait-timeout.md`（D115 / D116） |
| インストーラの使用許諾 | `docs/terms.md` から生成した利用規約を Setup の EULA として表示した | `handoff-installer-eula.md`（D117〜D119） |

**ライセンスと公開範囲の決定は 18章 18.2。** リポジトリ3構成とその理由もそこにある。

**公開用スクリプトの文字化けから分かったこと。** Windows PowerShell 5.1 は **BOM の無い `.ps1` を ANSI（CP932）として読む。** 日本語のリテラルを含むスクリプトは **UTF-8 BOM 付きで保存しなければならない。** また .NET Framework 上で動くため `Path.GetRelativePath` が無い。**この2点は今後 PowerShell スクリプトを追加するときの前提である。**

### テストの「全件合格」が偶然だった件

**出荷判断までに繰り返し確認していた「507/507 合格」は、実行順に依存した偶然だった。** `Phase7bRemediationTests` の1件は、**別のテストがテーマ辞書を共有の `Application.Resources` へ残していったときだけ通っていた。** エディタのビルドホストを終了させて実行順が変わったことで表面化した。**持ち越し3件目の「失敗0件を確認した」という結論は、この範囲では正しくなかった。**

続けて、待機の不備が3種見つかった。

| 分類 | 内容 | 決定 |
| --- | --- | --- |
| 他のテストの副作用に依存 | 共有の `Application.Resources` に依存していた。**対象ウィンドウ内へテーマ辞書を入れて自己完結させた** | D111 |
| 待つ信号と検証する状態の不一致 | `ApplyPresentation` の**1行目で完了する信号**を待って、**最終行で決まる状態**を検証していた | D113 |
| 待機が無期限 | 条件が成立しないとテストが終わらず、**何を待っていたのか分からない** | D115 / D116 |

**待機の仕組みは3系統（`WaitUntilAsync` / `PumpDispatcherUntil` / `TaskCompletionSource`）あり、97箇所を点検して9箇所を是正した。** D84 の是正が `WaitUntilAsync` の22箇所しか見ていなかったのは**指示側の範囲設定の漏れである。**

### インストーラの使用許諾（D117〜D119）

`LicenseFile` で表示する。**`docs/terms.md` から `artifacts\installer-input\TERMS.txt` を生成する**（Inno Setup が平文・CRLF・UTF-8 BOM を要求するため、front matter と Markdown 記法を落として変換する）。**規約の原本を1つに保つ**ため、公開ページ（GitHub Pages）とインストーラは同じ `docs/terms.md` を出所とする。生成物は `{app}` へも同梱する。**同意しなければインストールを続行できない。** 実機で、表示されること・文字化けが無いこと・段落が繋がらないこと・`{app}` に同梱されることを確認した。

### 出荷後に残る事項

- **Google の公開ステータス切り替え（2026-09-08）で7日ごとの再認証が解けたことの実測。** **2026-09-15 以降**に再認証を求められないことを確認する。それまで 8.6 の記述は見込みである。

- **`build-installer.ps1 -NoSign` は署名済みインストーラを同名で黙って上書きする。** 実際に一度上書きした。出力ファイル名を分ける案は採らず、**配布前に必ず `build.ps1` → `build-installer.ps1`（既定で署名する）を通す手順で担保する。**

- **資格情報の環境変数名の綴り違いは検出できない。** `UnifiedCalendar__Google_ClientSecret`（アンダースコア1つ）と書いた事故が起きたが、D98 の歯止めは**不在を警告するだけで、近い名前の存在を指摘できない。** 綴り違いの検知と、資格情報をファイルから読む `build-release.ps1` は**保留とした。**

## 1.1.0 の作業（2026-09-08 着手）

**1.0.0 の出荷後に受けた3件の機能追加である。** 製品の機能を変えるため、基準仕様書と本書を先に改訂し、そのうえで実装の指示書を出した。

| # | 内容 | 決定 | 指示書 | 受入記号 |
| --- | --- | --- | --- | --- |
| 1 | メイン画面から設定画面を開く歯車ボタン | D120〜D122 | `handoff-main-window-settings-button.md` | KK |
| 2 | メイン画面の最小幅を 560 → 370 DIP | D123〜D126 | `handoff-main-window-minimum-width.md` | LL |
| 3 | 予定開始前の通知 | D127〜D136 | `handoff-event-start-notifications.md` | MM |

### 決定記録

| 決定 | 内容 |
| --- | --- |
| D120 | 歯車ボタンをステータス領域の右端（手動更新ボタンの右）に置く。`Segoe MDL2 Assets`の`E713`、`Width=32`、高さは`WarningButtonHeight`。新規リソース`Status.Settings`を使い、トレイの`Tray.Settings`を流用しない |
| D121 | コマンドは`MainWindowViewModel`に持たせ、`ISettingsWindowLauncher`は**両ctorの末尾の任意パラメータ**で注入する（既存の呼び出しを壊さないため）。失敗は`MainWindowActionFailed`で警告し、例外をUIへ伝播させない |
| D122 | `IsAvailable=false`のときは非表示にせず無効化する。Tab順は 更新`0` → 歯車`1` → 一覧`2` → 未登録状態の追加ボタン`3`/`4`。専用ショートカットは設けない（KEY-010を維持） |
| D123 | `LayoutMetrics.MinimumMainWidth`を`560d`→`370d`。最小高さ・初期サイズ・設定画面の値は変えない |
| D124 | ステータス領域の最終正常更新時刻の列を可変幅（`*`）にし、`TextTrimming=CharacterEllipsis`で切る。現在時刻・警告・更新ボタン・歯車は切らせない。**370を成立させるための必須の変更である**（既定フォントでの必要幅は、最終更新を含めると約392、除くと約244と見積った） |
| D125 | 最小内容幅の算出を独立したクラスへ置き、**フォント10 / 14 / 24 DIPのいずれでも370以下であること**をテストで固定する。`Window.MinWidth`は定数のまま束縛し、算出値は検証専用とする |
| D126 | 詳細ポップアップの最大幅520は変えない。メインウィンドウより広くなることを許容し、画面外へ出ないことは既存の配置補正に委ねる |
| D127 | 解禁するのは予定開始前の通知だけ。同期エラー・レート制限・予定終了の通知とアイコン変更は引き続き行わない。サウンドの設定項目は設けない |
| D128 | 方式は`NotifyIcon.ShowBalloonTip`。`ITrayIconAdapter`へメソッドを追加し、`Phase7ShellTests`の契約テストは**消さずに反転**する。`BalloonTipText`/`BalloonTipTitle`/`Icon`/`PlaySound`の禁止は残す |
| D129 | `NotificationPreferences(Enabled = true, LeadMinutes = 5)`、範囲1〜60。`AppSettings`へは末尾追加。**既定ONのため、1.0.0から更新した利用者は設定を触らずに通知が出始める** |
| D130 | settings.jsonをv2へ。`SettingsSchemaV1ToV2Migration`を追加し、登録はDIと`SettingsJsonStore`の既定配列の2箇所。v1を隔離してはならない。**v2を1.0.0で読むとnewer判定で退避され、アカウント登録が失われる**（既存仕様どおりの挙動として据え置く） |
| D131 | 判定は分境界の`InternalRefreshSignal`へ相乗りし、新しいタイマーを作らない。投影は`EventNotificationHostedService`が自前で行い、`MainWindowViewModel`に通知の責務を持たせない。1分あたりの二重投影のコストは計測して判断する |
| D132 | 同じ判定で複数該当したときは1通に集約する（最も早い1件＋件数）。該当した全件を既通知として登録する |
| D133 | 対象は時刻付き予定のみ。キーは`StableId`と開始時刻の組。絞り込みは表示と同じ`CalendarPresentationService`の経路を通す。既通知集合は永続化しない |
| D134 | 文面は`Notification.Title` / `Body` / `BodyMore`。組み立てはApp側で行い、Coreのplannerは値を返すだけにする。本文は200文字で切る。ログに件名・場所・URLを出さない |
| D135 | 通知のクリック動作は実装しない。サウンドはOSの既定に従う。`timeoutMilliseconds`は固定値10000とし、設定項目にしない（近年のWindowsは無視する） |
| D136 | 設定カテゴリ『通知』を`update`と`general`の間へ追加し7カテゴリにする。変更は即時反映。`Enabled=false`のとき分のスピナーを無効化する。SET-019の初期化対象に通知設定を含める |

### 改訂した文書（2026-09-08）

| 文書 | 改訂 |
| --- | --- |
| `specification.md` | 版1.1へ。製品方針、FUN-010（新設）、UI-002、UI-004、UI-013、SET-019、SET-021 / SET-022（新設）、ERR-013、WIN-003、WIN-015、**13.3 通知（新設、NTF-001〜NTF-010）**、13章の章題、18章の対象外、ACC-011、ACC-013（新設）、目次 |
| `technical-design/19-notifications.md` | **新設。** 通知の設計 |
| `technical-design/10-settings-cache-schema.md` | settings.json v2、`notifications`、`leadMinutes`の検証、移行とダウングレードの注記 |
| `technical-design/13-window-tray-single-instance-dpi.md` | メインの寸法（最小幅370）、設定ボタン、トレイのバルーン通知 |
| `technical-design/15-testing-and-acceptance.md` | 章題をACC-013まで、ACC-011の見直し、ACC-013の追加、品質ゲート |
| `technical-design/18-product-tbd-and-out-of-scope.md` | 18.1から通知を外し、引き続き対象外とするものを明示 |
| `technical-design/README.md` | 19章の追加、15章の章題、1.1.0の言及 |

### 出荷時にやること

- **`docs/index.md` と `docs/terms.md` の「通知は提供しません」を直す。** どちらも公開ページであり、`terms.md` はインストーラのEULAの出所でもある。**1.1.0を出すときに直す。** 実装前に直すと、出荷済みの1.0.0の説明として誤りになる。

- `src/UnifiedCalendar.App/UnifiedCalendar.App.csproj` の `Version` を `1.1.0` へ上げる。**3件がそろってから行う。** 各指示書では触らせない。インストーラのファイル名（`UnifiedCalendar-Setup-1.1.0.exe`）が連動する。

- `installer\UnifiedCalendar.iss` の `AppId` は変えない。上書きインストールで1.0.0を置き換える。

- **ダウングレードの注意を配布時に伝える。** 1.1.0を入れた後に1.0.0へ戻すと、設定がnewer判定で退避されアカウントの再認証が必要になる（10.2）。

- `docs/acceptance-*.md`（非公開）へ実機受入の記録を残す。ACC-013と、370 DIPでの見え方、集中モード時の通知の挙動を含める。
